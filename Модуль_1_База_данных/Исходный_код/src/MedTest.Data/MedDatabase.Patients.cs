using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

public sealed partial class MedDatabase
{
    const string PatientColumns = "p.Id,p.CardNumber,p.LastName,p.FirstName,p.MiddleName,p.BirthDate,p.Sex,p.Phone,p.Note,p.IsArchived";

    static Patient MapPatient(SqlDataReader r) => new((int)r["Id"], (string)r["CardNumber"], (string)r["LastName"], (string)r["FirstName"],
        r.NullableString("MiddleName"), r.Date("BirthDate"), (string)r["Sex"] == "M" ? Sex.Male : Sex.Female, r.NullableString("Phone"), r.NullableString("Note"), (bool)r["IsArchived"]);

    /// <summary>
    /// Patient directory with the date of the latest examination and the number of indicators
    /// whose latest value is outside the reference range.
    /// </summary>
    public async Task<List<PatientRow>> ListPatientsAsync(AppUser actor)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin, Role.Doctor);
        return await Sql.ListAsync(c, null, $"""
            SELECT {PatientColumns}, last.TakenAt AS LastExamAt, ISNULL(cnt.N,0) AS ExamCount, ISNULL(dev.N,0) AS Deviations
            FROM dbo.Patients p
            OUTER APPLY (SELECT MAX(e.TakenAt) AS TakenAt FROM dbo.Examinations e WHERE e.PatientId=p.Id) last
            OUTER APPLY (SELECT COUNT(*) AS N FROM dbo.Examinations e WHERE e.PatientId=p.Id) cnt
            OUTER APPLY (
                SELECT COUNT(*) AS N FROM (
                    SELECT v.IndicatorId, v.Value, ROW_NUMBER() OVER (PARTITION BY v.IndicatorId ORDER BY e.TakenAt DESC, e.Id DESC) AS rn
                    FROM dbo.Examinations e JOIN dbo.ExaminationValues v ON v.ExaminationId=e.Id
                    WHERE e.PatientId=p.Id) x
                JOIN dbo.Indicators i ON i.Id=x.IndicatorId
                WHERE x.rn=1 AND (x.Value<i.RefMin OR x.Value>i.RefMax)) dev
            ORDER BY p.LastName, p.FirstName, p.MiddleName, p.Id
            """,
            r => new PatientRow(MapPatient(r), r["LastExamAt"] is DBNull ? null : (DateTime)r["LastExamAt"], (int)r["ExamCount"], (int)r["Deviations"]));
    }

    public async Task<Patient> GetPatientAsync(AppUser actor, int id)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin, Role.Doctor);
        return await LoadPatient(c, null, id);
    }

    static async Task<Patient> LoadPatient(SqlConnection c, SqlTransaction? tx, int id) =>
        (await Sql.ListAsync(c, tx, $"SELECT {PatientColumns} FROM dbo.Patients p WHERE p.Id=@id", MapPatient, ("@id", id))).SingleOrDefault()
        ?? throw DomainException.NotFound("Пациент не найден. Обновите список.");

    public async Task<Patient> CreatePatientAsync(AppUser actor, PatientInput input)
    {
        var p = Rules.Patient(input, Today);
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin, Role.Doctor);
        var id = Convert.ToInt32(await Sql.ScalarAsync(c, tx,
            "INSERT dbo.Patients(LastName,FirstName,MiddleName,BirthDate,Sex,Phone,Note,CreatedBy) OUTPUT inserted.Id VALUES(@l,@f,@m,@b,@s,@ph,@n,@u)",
            ("@l", p.LastName), ("@f", p.FirstName), ("@m", p.MiddleName), ("@b", p.BirthDate), ("@s", p.Sex == Sex.Male ? "M" : "F"), ("@ph", p.Phone), ("@n", p.Note), ("@u", actor.Id)));
        await Audit(c, tx, actor, "patient.create", "Patient", id);
        var patient = await LoadPatient(c, tx, id);
        await tx.CommitAsync();
        return patient;
    }

    public async Task<Patient> UpdatePatientAsync(AppUser actor, int id, PatientInput input)
    {
        var p = Rules.Patient(input, Today);
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin, Role.Doctor);
        var firstExam = await Sql.ScalarAsync(c, tx, "SELECT MIN(TakenAt) FROM dbo.Examinations WHERE PatientId=@id", ("@id", id));
        if (firstExam is DateTime first && DateOnly.FromDateTime(first) < p.BirthDate)
            throw DomainException.Validation("Дата рождения позже первого обследования пациента (" + first.ToString("dd.MM.yyyy") + ").");
        int changed = await Sql.RunAsync(c, tx, "UPDATE dbo.Patients SET LastName=@l,FirstName=@f,MiddleName=@m,BirthDate=@b,Sex=@s,Phone=@ph,Note=@n WHERE Id=@id",
            ("@l", p.LastName), ("@f", p.FirstName), ("@m", p.MiddleName), ("@b", p.BirthDate), ("@s", p.Sex == Sex.Male ? "M" : "F"), ("@ph", p.Phone), ("@n", p.Note), ("@id", id));
        if (changed == 0) throw DomainException.NotFound("Пациент не найден. Обновите список.");
        await Audit(c, tx, actor, "patient.update", "Patient", id);
        var patient = await LoadPatient(c, tx, id);
        await tx.CommitAsync();
        return patient;
    }

    /// <summary>Moves a patient to the archive or back. Archived patients keep their card but get no new examinations.</summary>
    public async Task SetArchivedAsync(AppUser actor, int id, bool archived)
    {
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin, Role.Doctor);
        if (await Sql.RunAsync(c, tx, "UPDATE dbo.Patients SET IsArchived=@a WHERE Id=@id", ("@a", archived), ("@id", id)) == 0)
            throw DomainException.NotFound("Пациент не найден. Обновите список.");
        await Audit(c, tx, actor, archived ? "patient.archive" : "patient.restore", "Patient", id);
        await tx.CommitAsync();
    }

    /// <summary>Deletes a patient entered by mistake. Only an administrator, and only while the patient has no examinations.</summary>
    public async Task DeletePatientAsync(AppUser actor, int id)
    {
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin);
        if (Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.Examinations WITH (UPDLOCK, HOLDLOCK) WHERE PatientId=@id", ("@id", id))) > 0)
            throw DomainException.Validation("У пациента есть обследования. Удалить его нельзя — переведите в архив.");
        if (await Sql.RunAsync(c, tx, "DELETE dbo.Patients WHERE Id=@id", ("@id", id)) == 0) throw DomainException.NotFound("Пациент не найден. Обновите список.");
        await Audit(c, tx, actor, "patient.delete", "Patient", id);
        await tx.CommitAsync();
    }
}
