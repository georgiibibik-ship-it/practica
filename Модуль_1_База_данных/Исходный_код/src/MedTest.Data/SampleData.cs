using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

/// <summary>Fills an empty database with patients and examinations so that the statistics and charts have data from the first launch.</summary>
public static class SampleData
{
    static readonly string[] MaleLast = ["Иванов", "Смирнов", "Кузнецов", "Попов", "Васильев", "Петров", "Соколов", "Михайлов", "Новиков", "Фёдоров", "Морозов", "Волков", "Алексеев", "Лебедев", "Семёнов", "Егоров", "Павлов", "Козлов", "Степанов", "Николаев"];
    static readonly string[] MaleFirst = ["Александр", "Дмитрий", "Максим", "Сергей", "Андрей", "Алексей", "Артём", "Илья", "Кирилл", "Михаил", "Никита", "Роман", "Егор", "Павел", "Владимир"];
    static readonly string[] MaleMiddle = ["Александрович", "Дмитриевич", "Сергеевич", "Андреевич", "Алексеевич", "Игоревич", "Николаевич", "Викторович", "Олегович", "Петрович"];
    static readonly string[] FemaleFirst = ["Анна", "Мария", "Елена", "Ольга", "Наталья", "Екатерина", "Татьяна", "Ирина", "Светлана", "Юлия", "Дарья", "Ксения", "Полина", "Виктория", "Алина"];
    static readonly string[] FemaleMiddle = ["Александровна", "Дмитриевна", "Сергеевна", "Андреевна", "Алексеевна", "Игоревна", "Николаевна", "Викторовна", "Олеговна", "Петровна"];

    /// <summary>Typical value and spread of each indicator in the generated group (adults).</summary>
    static readonly Dictionary<string, (double Mean, double Sd)> Profile = new()
    {
        ["SBP"] = (124, 14), ["DBP"] = (79, 9), ["HR"] = (74, 10),
        ["HGB"] = (138, 14), ["RBC"] = (4.6, 0.45), ["WBC"] = (6.5, 1.7), ["PLT"] = (250, 55), ["ESR"] = (9, 5),
        ["GLU"] = (5.0, 0.8), ["CHOL"] = (5.0, 0.9), ["CREA"] = (88, 17)
    };

    /// <summary>Adds the sample only to an empty patient directory; returns the number of patients added.</summary>
    public static async Task<int> FillAsync(string connectionString, string doctorLogin, int patients = 36, int seed = 20261105, DateTime? now = null)
    {
        var today = now ?? DateTime.Now;
        var random = new Random(seed);
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        if (Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.Patients")) > 0) { await tx.RollbackAsync(); return 0; }
        var doctor = await Sql.ScalarAsync(c, tx, "SELECT Id FROM dbo.Users WHERE Login=@l AND Role='Doctor'", ("@l", doctorLogin))
            ?? throw new InvalidOperationException("Врач " + doctorLogin + " не найден.");
        var indicators = await Sql.ListAsync(c, tx, "SELECT Id, Code, ValidMin, ValidMax, Decimals FROM dbo.Indicators WHERE IsActive=1",
            r => (Id: (int)r["Id"], Code: (string)r["Code"], Min: (decimal)r["ValidMin"], Max: (decimal)r["ValidMax"], Decimals: Convert.ToInt32(r["Decimals"])));
        double Normal() => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

        for (int n = 0; n < patients; n++)
        {
            bool male = random.Next(2) == 0;
            var last = MaleLast[random.Next(MaleLast.Length)];
            if (!male) last = last.EndsWith("ий") ? last[..^2] + "ая" : last + "а";
            var first = male ? MaleFirst[random.Next(MaleFirst.Length)] : FemaleFirst[random.Next(FemaleFirst.Length)];
            var middle = male ? MaleMiddle[random.Next(MaleMiddle.Length)] : FemaleMiddle[random.Next(FemaleMiddle.Length)];
            var birth = DateOnly.FromDateTime(today).AddYears(-random.Next(19, 78)).AddDays(-random.Next(365));
            var phone = "+79" + random.Next(100000000, 999999999);
            var patientId = Convert.ToInt32(await Sql.ScalarAsync(c, tx,
                "INSERT dbo.Patients(LastName,FirstName,MiddleName,BirthDate,Sex,Phone,CreatedBy) OUTPUT inserted.Id VALUES(@l,@f,@m,@b,@s,@ph,@u)",
                ("@l", last), ("@f", first), ("@m", middle), ("@b", birth), ("@s", male ? "M" : "F"), ("@ph", phone), ("@u", doctor)));

            // A personal level per indicator (some people are above the norm), then visits around it.
            var level = indicators.ToDictionary(i => i.Id, i => Profile.TryGetValue(i.Code, out var p) ? p.Mean + 0.8 * p.Sd * Normal() : 0);
            int visits = random.Next(1, 6);
            var at = today.AddDays(-random.Next(400, 720)).Date.AddHours(8 + random.Next(9)).AddMinutes(random.Next(0, 4) * 15);
            for (int v = 0; v < visits; v++)
            {
                var examId = Convert.ToInt32(await Sql.ScalarAsync(c, tx, "INSERT dbo.Examinations(PatientId,DoctorId,TakenAt) OUTPUT inserted.Id VALUES(@p,@d,@t)",
                    ("@p", patientId), ("@d", doctor), ("@t", at)));
                bool blood = v == 0 || random.Next(3) > 0;
                foreach (var i in indicators)
                {
                    if (!Profile.TryGetValue(i.Code, out var p)) continue;
                    if (!blood && i.Code is not ("SBP" or "DBP" or "HR")) continue;
                    var value = Math.Round((decimal)(level[i.Id] + p.Sd * 0.35 * Normal()), i.Decimals);
                    value = Math.Clamp(value, i.Min, i.Max);
                    await Sql.RunAsync(c, tx, "INSERT dbo.ExaminationValues(ExaminationId,IndicatorId,Value) VALUES(@e,@i,@v)", ("@e", examId), ("@i", i.Id), ("@v", value));
                }
                at = at.AddDays(random.Next(45, 150));
                if (at > today.AddDays(-1)) break;
            }
        }
        await tx.CommitAsync();
        return patients;
    }
}
