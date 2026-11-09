using MedTest.Core;
using MedTest.Data;

namespace MedTest.Tests;

/// <summary>Пункты А и Б, права ролей и установка базы — на настоящем SQL Server.</summary>
[Collection("db")]
public class DataTests(DatabaseFixture f)
{
    static PatientInput Person(string last = "Тестова", string first = "Анна", Sex sex = Sex.Female, string birth = "1990-04-12") =>
        new(last, first, "Сергеевна", DateOnly.Parse(birth), sex, "8 (900) 123-45-67", null);

    async Task<Dictionary<string, Indicator>> Indicators() => (await f.Db.ListIndicatorsAsync(f.Doctor)).ToDictionary(i => i.Code);

    async Task<Patient> NewPatient(string last = "Тестова") => await f.Db.CreatePatientAsync(f.Doctor, Person(last));

    [Fact]
    public async Task Setup_is_repeatable_and_seeds_reference_norms()
    {
        await DatabaseSetup.EnsureAsync(f.ConnectionString);
        var all = await Indicators();
        // Другие тесты этой же базы добавляют свои показатели, поэтому проверяются начальные коды.
        Assert.Superset(new HashSet<string> { "SBP", "DBP", "HR", "HGB", "RBC", "WBC", "PLT", "ESR", "GLU", "CHOL", "CREA" }, all.Keys.ToHashSet());
        Assert.Equal(90m, all["SBP"].RefMin); Assert.Equal(139m, all["SBP"].RefMax);
        Assert.Equal("ммоль/л", all["GLU"].Unit);
        // Единицы без степеней: врач видит «млн/мкл» и «тыс/мкл», числа те же.
        Assert.Equal("млн/мкл", all["RBC"].Unit);
        Assert.Equal("тыс/мкл", all["WBC"].Unit);
        Assert.Equal("тыс/мкл", all["PLT"].Unit);
        Assert.DoesNotContain(all.Values, i => i.Unit.Contains('^') || i.Unit.Contains('×'));
        Assert.False(await DatabaseSetup.CreateFirstUserAsync(f.ConnectionString, Role.Admin, "second_admin", "x", DatabaseFixture.Password));
    }

    [Fact]
    public async Task Existing_database_is_upgraded_on_sign_in_and_missing_one_is_not_created()
    {
        // База предыдущей версии: прежние единицы, миграция 2 ещё не применена.
        await using (var c = new Microsoft.Data.SqlClient.SqlConnection(f.ConnectionString))
        {
            await c.OpenAsync();
            await Sql.RunAsync(c, null, "UPDATE dbo.Indicators SET Unit=N'×10^12/л' WHERE Code='RBC'; UPDATE dbo.Indicators SET Unit=N'×10^9/л' WHERE Code IN ('WBC','PLT'); DELETE dbo.SchemaMigrations WHERE Version=2");
        }
        Assert.True(await DatabaseSetup.UpgradeAsync(f.ConnectionString));
        var all = await Indicators();
        Assert.Equal("млн/мкл", all["RBC"].Unit);
        Assert.Equal("тыс/мкл", all["PLT"].Unit);

        var missing = DatabaseFixture.ServerConnection("MedTest_Test_" + Guid.NewGuid().ToString("N"));
        Assert.False(await DatabaseSetup.UpgradeAsync(missing));
        await using var m = new Microsoft.Data.SqlClient.SqlConnection(DatabaseFixture.ServerConnection("master"));
        await m.OpenAsync();
        Assert.True(await Sql.ScalarAsync(m, null, "SELECT DB_ID(@db)", ("@db", DatabaseSetup.DatabaseName(missing))) is null or DBNull, "базу создавать не нужно");
    }

    [Fact]
    public async Task First_run_creates_a_doctor_when_only_disabled_doctors_exist()
    {
        // База, созданная SQL-скриптами модуля 1: врач из примеров данных отключён и войти не может.
        var cs = DatabaseFixture.ServerConnection("MedTest_Test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await DatabaseSetup.EnsureAsync(cs);
            await using (var c = new Microsoft.Data.SqlClient.SqlConnection(cs))
            {
                await c.OpenAsync();
                await Sql.RunAsync(c, null, "INSERT dbo.Users(Login,FullName,Role,IsActive,PasswordHash,Salt,Iterations) VALUES('sample_doctor',N'Врач (примеры данных)','Doctor',0,CRYPT_GEN_RANDOM(32),CRYPT_GEN_RANDOM(32),210000)");
            }
            Assert.True(await DatabaseSetup.CreateFirstUserAsync(cs, Role.Doctor, "real_doctor", "Лебедев Андрей Викторович", DatabaseFixture.Password));
            var doctor = await new MedDatabase(cs).LoginAsync("real_doctor", DatabaseFixture.Password, Role.Doctor);
            Assert.Equal(Role.Doctor, doctor.Role);
            Assert.False(await DatabaseSetup.CreateFirstUserAsync(cs, Role.Doctor, "second_doctor", "x", DatabaseFixture.Password));
        }
        finally { await DatabaseSetup.DropTestDatabaseAsync(cs); }
    }

    [Fact]
    public async Task Test_database_guard_refuses_working_names()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSetup.DropTestDatabaseAsync(DatabaseFixture.ServerConnection(DatabaseSetup.WorkingDatabase)));
        Assert.Throws<ArgumentException>(() => DatabaseSetup.DatabaseName(DatabaseFixture.ServerConnection("master")));
    }

    [Fact]
    public async Task Login_checks_password_mode_and_blocking()
    {
        var wrong = await Assert.ThrowsAsync<DomainException>(() => f.Db.LoginAsync("test_doctor", "не-тот-пароль", Role.Doctor));
        Assert.Equal("Неверный логин или пароль.", wrong.Message);
        var unknown = await Assert.ThrowsAsync<DomainException>(() => f.Db.LoginAsync("nobody_here", "не-тот-пароль", Role.Doctor));
        Assert.Equal(wrong.Message, unknown.Message);
        var mode = await Assert.ThrowsAsync<DomainException>(() => f.Db.LoginAsync("test_doctor", DatabaseFixture.Password, Role.Admin));
        Assert.Contains("«Врач»", mode.Message);

        var temp = await f.Db.SaveUserAsync(f.Admin, null, new UserInput("blocked_doc", "Врач Заблокированный", Role.Doctor, true, DatabaseFixture.Password));
        await f.Db.SaveUserAsync(f.Admin, temp.Id, new UserInput("blocked_doc", "Врач Заблокированный", Role.Doctor, false, null));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.LoginAsync("blocked_doc", DatabaseFixture.Password, Role.Doctor));
        // An already open session of a blocked account cannot change data.
        var e = await Assert.ThrowsAsync<DomainException>(() => f.Db.CreatePatientAsync(temp, Person()));
        Assert.Equal("forbidden", e.Code);
    }

    [Fact]
    public async Task A_patient_directory_create_find_update_archive()
    {
        var p = await NewPatient("Справочникова");
        Assert.Matches(@"\APT-\d{6}\z", p.CardNumber);
        Assert.Equal("Справочникова Анна Сергеевна", p.FullName);
        Assert.Equal("+79001234567", p.Phone);

        var changed = await f.Db.UpdatePatientAsync(f.Admin, p.Id, Person("Справочникова-Новая", "Анна") with { Phone = "" , Note = "Аллергия на пенициллин" });
        Assert.Equal("Справочникова-Новая", changed.LastName);
        Assert.Null(changed.Phone);
        Assert.Equal("Аллергия на пенициллин", changed.Note);

        var list = await f.Db.ListPatientsAsync(f.Doctor);
        var row = Assert.Single(list, r => r.Patient.Id == p.Id);
        Assert.Equal(0, row.ExamCount); Assert.Null(row.LastExamAt);

        await f.Db.SetArchivedAsync(f.Doctor, p.Id, true);
        Assert.True((await f.Db.GetPatientAsync(f.Doctor, p.Id)).IsArchived);
        await f.Db.SetArchivedAsync(f.Doctor, p.Id, false);
        Assert.False((await f.Db.GetPatientAsync(f.Admin, p.Id)).IsArchived);
    }

    [Theory]
    [InlineData("", "Анна", "1990-01-01", "Фамилия")]
    [InlineData("Иванова2", "Анна", "1990-01-01", "Фамилия")]
    [InlineData("Иванова", "Анна", "2030-01-01", "Дата рождения")]
    [InlineData("Иванова", "Анна", "1899-12-31", "Дата рождения")]
    public async Task A_patient_input_is_validated(string last, string first, string birth, string field)
    {
        var e = await Assert.ThrowsAsync<DomainException>(() => f.Db.CreatePatientAsync(f.Doctor, Person(last, first, birth: birth)));
        Assert.Equal("validation", e.Code);
        Assert.Contains(field, e.Message);
    }

    [Fact]
    public async Task A_patient_with_examinations_is_archived_not_deleted()
    {
        var empty = await NewPatient("Ошибочная");
        await Assert.ThrowsAsync<DomainException>(() => f.Db.DeletePatientAsync(f.Doctor, empty.Id));
        await f.Db.DeletePatientAsync(f.Admin, empty.Id);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.GetPatientAsync(f.Admin, empty.Id));

        var used = await NewPatient("Наблюдаемая");
        var ind = await Indicators();
        await f.Db.SaveExaminationAsync(f.Doctor, null, new(used.Id, new DateTime(2026, 10, 1, 9, 30, 0), null, [new(ind["HR"].Id, 72)]));
        var e = await Assert.ThrowsAsync<DomainException>(() => f.Db.DeletePatientAsync(f.Admin, used.Id));
        Assert.Contains("архив", e.Message);
    }

    [Fact]
    public async Task B_doctor_records_pressure_and_tests_in_one_examination()
    {
        var p = await NewPatient("Давлениева");
        var ind = await Indicators();
        var exam = await f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 11, 1, 10, 15, 42), "Плановый осмотр",
            [new(ind["SBP"].Id, 145), new(ind["DBP"].Id, 92), new(ind["GLU"].Id, 5.26m), new(ind["RBC"].Id, 4.555m)]));
        Assert.Equal(new DateTime(2026, 11, 1, 10, 15, 0), exam.TakenAt);
        Assert.Equal(4, exam.Values.Count);
        Assert.Equal(5.3m, exam.Values.Single(v => v.IndicatorId == ind["GLU"].Id).Value);
        Assert.Equal(4.56m, exam.Values.Single(v => v.IndicatorId == ind["RBC"].Id).Value);
        Assert.Equal("Врач Первый", exam.DoctorName);

        var history = await f.Db.ListExaminationsAsync(f.Admin, p.Id);
        Assert.Equal(exam.Id, Assert.Single(history).Id);
        var row = (await f.Db.ListPatientsAsync(f.Doctor)).Single(r => r.Patient.Id == p.Id);
        Assert.Equal(1, row.ExamCount);
        Assert.Equal(2, row.Deviations); // давление выше нормы, глюкоза и эритроциты в норме
    }

    [Fact]
    public async Task B_wrong_input_is_refused()
    {
        var p = await NewPatient("Ошибкина");
        var ind = await Indicators();
        var at = new DateTime(2026, 11, 2, 9, 0, 0);
        var none = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, at, null, [])));
        Assert.Contains("хотя бы один", none.Message);
        var typo = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, at, null, [new(ind["SBP"].Id, 1200)])));
        Assert.Contains("вне допустимого диапазона", typo.Message);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, at, null, [new(ind["HR"].Id, 70), new(ind["HR"].Id, 71)])));
        var future = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 11, 6), null, [new(ind["HR"].Id, 70)])));
        Assert.Contains("будущем", future.Message);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(1989, 1, 1), null, [new(ind["HR"].Id, 70)])));
        Assert.Empty(await f.Db.ListExaminationsAsync(f.Doctor, p.Id));
    }

    [Fact]
    public async Task B_roles_admin_reads_doctor_corrects_only_own_examinations()
    {
        var p = await NewPatient("Ролевая");
        var ind = await Indicators();
        var at = new DateTime(2026, 9, 1, 9, 0, 0);
        var forbidden = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Admin, null, new(p.Id, at, null, [new(ind["HR"].Id, 70)])));
        Assert.Equal("forbidden", forbidden.Code);

        var exam = await f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, at, null, [new(ind["HR"].Id, 70), new(ind["GLU"].Id, 6.1m)]));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.OtherDoctor, exam.Id, new(p.Id, at, null, [new(ind["HR"].Id, 71)])));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.DeleteExaminationAsync(f.OtherDoctor, exam.Id));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.DeleteExaminationAsync(f.Admin, exam.Id));

        var fixedExam = await f.Db.SaveExaminationAsync(f.Doctor, exam.Id, new(p.Id, at.AddHours(1), "исправлено", [new(ind["HR"].Id, 68)]));
        Assert.Equal(68m, Assert.Single(fixedExam.Values).Value);
        Assert.Equal("исправлено", fixedExam.Comment);
        await f.Db.DeleteExaminationAsync(f.Doctor, exam.Id);
        Assert.Empty(await f.Db.ListExaminationsAsync(f.Doctor, p.Id));
    }

    [Fact]
    public async Task B_archived_patient_gets_no_new_examination()
    {
        var p = await NewPatient("Архивная");
        var ind = await Indicators();
        await f.Db.SetArchivedAsync(f.Doctor, p.Id, true);
        var e = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 10, 1), null, [new(ind["HR"].Id, 70)])));
        Assert.Contains("архиве", e.Message);
    }

    [Fact]
    public async Task Deviation_count_follows_the_latest_value_of_each_indicator()
    {
        var p = await NewPatient("Динамикова");
        var ind = await Indicators();
        await f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 3, 1, 9, 0, 0), null, [new(ind["GLU"].Id, 7.2m), new(ind["CHOL"].Id, 6.4m)]));
        Assert.Equal(2, (await f.Db.ListPatientsAsync(f.Doctor)).Single(r => r.Patient.Id == p.Id).Deviations);
        // Later only glucose was measured and it is normal now; cholesterol keeps its last (high) value.
        await f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 6, 1, 9, 0, 0), null, [new(ind["GLU"].Id, 5.0m)]));
        var row = (await f.Db.ListPatientsAsync(f.Doctor)).Single(r => r.Patient.Id == p.Id);
        Assert.Equal(1, row.Deviations);
        Assert.Equal(new DateTime(2026, 6, 1, 9, 0, 0), row.LastExamAt);

        var points = await f.Db.SamplePointsAsync(f.Doctor, p.Id);
        Assert.Equal(3, points.Count);
        Assert.All(points, pt => Assert.Equal(p.Id, pt.PatientId));
    }

    [Fact]
    public async Task Indicator_norms_are_edited_by_admin_only()
    {
        var input = new IndicatorInput("ALT", "Биохимия крови", "АЛТ", "Ед/л", 0, 41, 0, 2000, 0, 120, true);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveIndicatorAsync(f.Doctor, null, input));
        var alt = await f.Db.SaveIndicatorAsync(f.Admin, null, input);
        Assert.Equal("ALT", alt.Code);
        var dup = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveIndicatorAsync(f.Admin, null, input with { Name = "Ещё АЛТ" }));
        Assert.Contains("уже есть", dup.Message);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveIndicatorAsync(f.Admin, alt.Id, input with { RefMin = 50 }));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveIndicatorAsync(f.Admin, alt.Id, input with { ValidMax = 30 }));

        var p = await NewPatient("Нормина");
        await f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 10, 1), null, [new(alt.Id, 300)]));
        var narrow = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveIndicatorAsync(f.Admin, alt.Id, input with { ValidMax = 200 }));
        Assert.Contains("вне нового допустимого диапазона", narrow.Message);

        var off = await f.Db.SaveIndicatorAsync(f.Admin, alt.Id, input with { IsActive = false, RefMax = 40 });
        Assert.False(off.IsActive); Assert.Equal(40m, off.RefMax);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveExaminationAsync(f.Doctor, null, new(p.Id, new DateTime(2026, 10, 2), null, [new(alt.Id, 30)])));
    }

    [Fact]
    public async Task Staff_accounts_are_managed_by_admin()
    {
        await Assert.ThrowsAsync<DomainException>(() => f.Db.ListUsersAsync(f.Doctor));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveUserAsync(f.Doctor, null, new UserInput("x_doc", "X", Role.Doctor, true, DatabaseFixture.Password)));
        var dup = await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveUserAsync(f.Admin, null, new UserInput("TEST_DOCTOR", "Дубль", Role.Doctor, true, DatabaseFixture.Password)));
        Assert.Contains("занят", dup.Message);
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveUserAsync(f.Admin, null, new UserInput("short_pw", "Короткий", Role.Doctor, true, "1234")));
        await Assert.ThrowsAsync<DomainException>(() => f.Db.SaveUserAsync(f.Admin, f.Admin.Id, new UserInput("test_admin", "Я", Role.Admin, false, null)));

        var nurse = await f.Db.SaveUserAsync(f.Admin, null, new UserInput("pw_change", "Смена Пароля", Role.Doctor, true, DatabaseFixture.Password));
        await f.Db.SaveUserAsync(f.Admin, nurse.Id, new UserInput("pw_change", "Смена Пароля", Role.Doctor, true, "Новый-пароль-2026"));
        await f.Db.LoginAsync("pw_change", "Новый-пароль-2026", Role.Doctor);
        Assert.Contains(await f.Db.ListUsersAsync(f.Admin), u => u.Login == "pw_change");
    }

    [Fact]
    public async Task C_sample_data_gives_every_indicator_a_statistical_norm()
    {
        var cs = DatabaseFixture.ServerConnection("MedTest_Test_" + Guid.NewGuid().ToString("N"));
        try
        {
            await DatabaseSetup.EnsureAsync(cs);
            await DatabaseSetup.CreateFirstUserAsync(cs, Role.Doctor, "sample_doc", "Врач Пример", DatabaseFixture.Password);
            Assert.Equal(36, await SampleData.FillAsync(cs, "sample_doc"));
            Assert.Equal(0, await SampleData.FillAsync(cs, "sample_doc"));
            var db = new MedDatabase(cs);
            var doctor = await db.LoginAsync("sample_doc", DatabaseFixture.Password, Role.Doctor);
            var points = await db.SamplePointsAsync(doctor);
            foreach (var i in await db.ListIndicatorsAsync(doctor))
            {
                var s = Statistics.Compute(Statistics.Sample(points, i.Id, SampleMode.LatestPerPatient), i);
                Assert.True(s.HasNorm, i.Code);
                Assert.InRange(s.N, 20, 36);
                Assert.All(points.Where(p => p.IndicatorId == i.Id), p => Assert.InRange(p.Value, i.ValidMin, i.ValidMax));
            }
            Assert.All(points, p => Assert.True(p.TakenAt <= DateTime.Now));
        }
        finally { await DatabaseSetup.DropTestDatabaseAsync(cs); }
    }
}
