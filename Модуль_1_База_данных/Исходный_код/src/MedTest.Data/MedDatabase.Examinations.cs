using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

public sealed partial class MedDatabase
{
    const string IndicatorColumns = "Id,Code,GroupName,Name,Unit,RefMin,RefMax,ValidMin,ValidMax,Decimals,SortOrder,IsActive";

    static Indicator MapIndicator(SqlDataReader r) => new((int)r["Id"], (string)r["Code"], (string)r["GroupName"], (string)r["Name"], (string)r["Unit"],
        (decimal)r["RefMin"], (decimal)r["RefMax"], (decimal)r["ValidMin"], (decimal)r["ValidMax"], Convert.ToInt32(r["Decimals"]), (int)r["SortOrder"], (bool)r["IsActive"]);

    // ---------------- Справочник показателей ----------------

    public async Task<List<Indicator>> ListIndicatorsAsync(AppUser actor)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin, Role.Doctor);
        return await Sql.ListAsync(c, null, $"SELECT {IndicatorColumns} FROM dbo.Indicators ORDER BY SortOrder, Name", MapIndicator);
    }

    /// <summary>Adds (id null) or changes an indicator and its norm. Only an administrator.</summary>
    public async Task<Indicator> SaveIndicatorAsync(AppUser actor, int? id, IndicatorInput input)
    {
        var i = Rules.Indicator(input);
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin);
        if (Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.Indicators WHERE Code=@c AND Id<>ISNULL(@id,0)", ("@c", i.Code), ("@id", id))) > 0)
            throw DomainException.Validation("Код «" + i.Code + "» уже есть в справочнике.");
        if (id != null)
        {
            // Stored values must stay inside the accepted input range.
            var outside = Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.ExaminationValues WHERE IndicatorId=@id AND (Value<@min OR Value>@max)",
                ("@id", id), ("@min", i.ValidMin), ("@max", i.ValidMax)));
            if (outside > 0) throw DomainException.Validation($"Уже сохранено {outside} знач. вне нового допустимого диапазона. Расширьте диапазон ввода.");
        }
        object?[] values = [i.Code, i.Group, i.Name, i.Unit, i.RefMin, i.RefMax, i.ValidMin, i.ValidMax, (byte)i.Decimals, i.SortOrder, i.IsActive];
        var args = new[] { "@code", "@g", "@n", "@u", "@rmin", "@rmax", "@vmin", "@vmax", "@d", "@s", "@a" }.Zip(values, (n, v) => (n, v)).ToList();
        int indicatorId;
        if (id == null)
        {
            indicatorId = Convert.ToInt32(await Sql.ScalarAsync(c, tx,
                "INSERT dbo.Indicators(Code,GroupName,Name,Unit,RefMin,RefMax,ValidMin,ValidMax,Decimals,SortOrder,IsActive) OUTPUT inserted.Id VALUES(@code,@g,@n,@u,@rmin,@rmax,@vmin,@vmax,@d,@s,@a)", args.ToArray()));
            await Audit(c, tx, actor, "indicator.create", "Indicator", indicatorId, i.Code);
        }
        else
        {
            indicatorId = id.Value;
            args.Add(("@id", indicatorId));
            if (await Sql.RunAsync(c, tx, "UPDATE dbo.Indicators SET Code=@code,GroupName=@g,Name=@n,Unit=@u,RefMin=@rmin,RefMax=@rmax,ValidMin=@vmin,ValidMax=@vmax,Decimals=@d,SortOrder=@s,IsActive=@a WHERE Id=@id", args.ToArray()) == 0)
                throw DomainException.NotFound("Показатель не найден. Обновите список.");
            await Audit(c, tx, actor, "indicator.update", "Indicator", indicatorId, $"{i.Code}: норма {i.RefMin}–{i.RefMax}");
        }
        var saved = (await Sql.ListAsync(c, tx, $"SELECT {IndicatorColumns} FROM dbo.Indicators WHERE Id=@id", MapIndicator, ("@id", indicatorId))).Single();
        await tx.CommitAsync();
        return saved;
    }

    // ---------------- Обследования ----------------

    /// <summary>Examinations of a patient, newest first, with their values.</summary>
    public async Task<List<Examination>> ListExaminationsAsync(AppUser actor, int patientId)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin, Role.Doctor);
        return await LoadExaminations(c, null, "e.PatientId=@p", ("@p", patientId));
    }

    static async Task<List<Examination>> LoadExaminations(SqlConnection c, SqlTransaction? tx, string where, params (string, object?)[] args)
    {
        var heads = await Sql.ListAsync(c, tx, $"SELECT e.Id,e.PatientId,e.DoctorId,u.FullName,e.TakenAt,e.Comment FROM dbo.Examinations e JOIN dbo.Users u ON u.Id=e.DoctorId WHERE {where} ORDER BY e.TakenAt DESC, e.Id DESC",
            r => (Id: (int)r["Id"], Patient: (int)r["PatientId"], Doctor: (int)r["DoctorId"], Name: (string)r["FullName"], At: (DateTime)r["TakenAt"], Comment: r.NullableString("Comment")), args);
        var values = await Sql.ListAsync(c, tx, $"SELECT v.ExaminationId,v.IndicatorId,v.Value FROM dbo.ExaminationValues v JOIN dbo.Examinations e ON e.Id=v.ExaminationId JOIN dbo.Indicators i ON i.Id=v.IndicatorId WHERE {where} ORDER BY i.SortOrder",
            r => (Exam: (int)r["ExaminationId"], Value: new MeasuredValue((int)r["IndicatorId"], (decimal)r["Value"])), args);
        var byExam = values.ToLookup(v => v.Exam, v => v.Value);
        return heads.Select(h => new Examination(h.Id, h.Patient, h.Doctor, h.Name, h.At, h.Comment, byExam[h.Id].ToList())).ToList();
    }

    /// <summary>
    /// Records a new examination (id null) or corrects one. Only a doctor, and a doctor corrects only their own examinations.
    /// Empty indicators are simply not sent; at least one value is required.
    /// </summary>
    public async Task<Examination> SaveExaminationAsync(AppUser actor, int? id, ExaminationInput input)
    {
        var comment = Rules.Text(input.Comment, "Комментарий", 1000);
        if (input.Values.Count == 0) throw DomainException.Validation("Введите хотя бы один показатель.");
        if (input.Values.Select(v => v.IndicatorId).Distinct().Count() != input.Values.Count) throw DomainException.Validation("Показатель указан дважды.");
        var takenAt = new DateTime(input.TakenAt.Year, input.TakenAt.Month, input.TakenAt.Day, input.TakenAt.Hour, input.TakenAt.Minute, 0);
        if (takenAt > Now().AddMinutes(5)) throw DomainException.Validation("Дата обследования не может быть в будущем.");

        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Doctor);
        var patient = await LoadPatient(c, tx, input.PatientId);
        if (DateOnly.FromDateTime(takenAt) < patient.BirthDate) throw DomainException.Validation("Дата обследования раньше даты рождения пациента.");

        HashSet<int> previous = [];
        if (id != null)
        {
            var owner = await Sql.ListAsync(c, tx, "SELECT DoctorId, PatientId FROM dbo.Examinations WITH (UPDLOCK) WHERE Id=@id", r => (Doctor: (int)r["DoctorId"], Patient: (int)r["PatientId"]), ("@id", id));
            if (owner.Count == 0) throw DomainException.NotFound("Обследование не найдено. Обновите карту пациента.");
            if (owner[0].Patient != input.PatientId) throw DomainException.Validation("Обследование относится к другому пациенту.");
            if (owner[0].Doctor != actor.Id) throw DomainException.Forbidden("Исправлять обследование может только врач, который его внёс.");
            previous = (await Sql.ListAsync(c, tx, "SELECT IndicatorId FROM dbo.ExaminationValues WHERE ExaminationId=@id", r => (int)r["IndicatorId"], ("@id", id))).ToHashSet();
        }
        else if (patient.IsArchived) throw DomainException.Validation("Пациент в архиве. Верните его из архива, чтобы внести обследование.");

        var indicators = (await Sql.ListAsync(c, tx, $"SELECT {IndicatorColumns} FROM dbo.Indicators", MapIndicator)).ToDictionary(i => i.Id);
        var clean = new List<MeasuredValue>();
        foreach (var v in input.Values)
        {
            if (!indicators.TryGetValue(v.IndicatorId, out var indicator)) throw DomainException.Validation("Показатель не найден в справочнике. Обновите окно.");
            // A switched-off indicator stays editable only where it was already recorded.
            if (!indicator.IsActive && !previous.Contains(indicator.Id)) throw DomainException.Validation("Показатель «" + indicator.Name + "» отключён в справочнике.");
            clean.Add(new(indicator.Id, Rules.Value(v.Value, indicator)));
        }

        int examId;
        if (id == null)
        {
            examId = Convert.ToInt32(await Sql.ScalarAsync(c, tx, "INSERT dbo.Examinations(PatientId,DoctorId,TakenAt,Comment) OUTPUT inserted.Id VALUES(@p,@d,@t,@c)",
                ("@p", input.PatientId), ("@d", actor.Id), ("@t", takenAt), ("@c", comment)));
        }
        else
        {
            examId = id.Value;
            await Sql.RunAsync(c, tx, "UPDATE dbo.Examinations SET TakenAt=@t, Comment=@c, UpdatedAt=SYSUTCDATETIME() WHERE Id=@id", ("@t", takenAt), ("@c", comment), ("@id", examId));
            await Sql.RunAsync(c, tx, "DELETE dbo.ExaminationValues WHERE ExaminationId=@id", ("@id", examId));
        }
        foreach (var v in clean)
            await Sql.RunAsync(c, tx, "INSERT dbo.ExaminationValues(ExaminationId,IndicatorId,Value) VALUES(@e,@i,@v)", ("@e", examId), ("@i", v.IndicatorId), ("@v", v.Value));
        await Audit(c, tx, actor, id == null ? "exam.create" : "exam.update", "Examination", examId, $"пациент {patient.CardNumber}, показателей: {clean.Count}");
        var saved = (await LoadExaminations(c, tx, "e.Id=@id", ("@id", examId))).Single();
        await tx.CommitAsync();
        return saved;
    }

    /// <summary>Deletes an examination entered by mistake; only the doctor who entered it.</summary>
    public async Task DeleteExaminationAsync(AppUser actor, int id)
    {
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Doctor);
        var owner = await Sql.ScalarAsync(c, tx, "SELECT DoctorId FROM dbo.Examinations WITH (UPDLOCK) WHERE Id=@id", ("@id", id));
        if (owner == null) throw DomainException.NotFound("Обследование не найдено. Обновите карту пациента.");
        if ((int)owner != actor.Id) throw DomainException.Forbidden("Удалить обследование может только врач, который его внёс.");
        await Sql.RunAsync(c, tx, "DELETE dbo.Examinations WHERE Id=@id", ("@id", id));
        await Audit(c, tx, actor, "exam.delete", "Examination", id);
        await tx.CommitAsync();
    }

    // ---------------- Данные для статистики и графиков ----------------

    /// <summary>Every recorded value, optionally of one patient: the source for statistics, the card and the charts.</summary>
    public async Task<List<SamplePoint>> SamplePointsAsync(AppUser actor, int? patientId = null)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin, Role.Doctor);
        return await Sql.ListAsync(c, null,
            "SELECT e.PatientId, v.IndicatorId, e.TakenAt, v.Value FROM dbo.ExaminationValues v JOIN dbo.Examinations e ON e.Id=v.ExaminationId WHERE @p IS NULL OR e.PatientId=@p",
            r => new SamplePoint((int)r["PatientId"], (int)r["IndicatorId"], (DateTime)r["TakenAt"], (decimal)r["Value"]), ("@p", patientId));
    }
}
