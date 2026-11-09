namespace MedTest.Core;

public enum Role { Admin, Doctor }

/// <summary>Signed-in employee.</summary>
public sealed record AppUser(int Id, string Login, string FullName, Role Role, bool IsActive)
{
    public string RoleTitle => Roles.Title(Role);
}

public static class Roles
{
    public static string Title(Role role) => role == Role.Admin ? "Администратор" : "Врач";
    public static string Code(Role role) => role == Role.Admin ? "Admin" : "Doctor";
    public static Role Parse(string code) => code == "Admin" ? Role.Admin : code == "Doctor" ? Role.Doctor
        : throw new ArgumentException("Неизвестная роль: " + code);
}

public enum Sex { Male, Female }

public sealed record Patient(int Id, string CardNumber, string LastName, string FirstName, string? MiddleName, DateOnly BirthDate,
    Sex Sex, string? Phone, string? Note, bool IsArchived)
{
    public string FullName => string.Join(' ', new[] { LastName, FirstName, MiddleName }.Where(p => !string.IsNullOrWhiteSpace(p)));
    public string ShortName => LastName + " " + FirstName[0] + "." + (string.IsNullOrWhiteSpace(MiddleName) ? "" : " " + MiddleName[0] + ".");
    public int AgeOn(DateOnly day) => Ages.Years(BirthDate, day);
    public string SexTitle => Sex == Sex.Male ? "Мужской" : "Женский";
}

/// <summary>Fields of a patient typed in the directory form.</summary>
public sealed record PatientInput(string LastName, string FirstName, string? MiddleName, DateOnly BirthDate, Sex Sex, string? Phone, string? Note);

/// <summary>One line of the patient directory with the facts of the latest examination.</summary>
public sealed record PatientRow(Patient Patient, DateTime? LastExamAt, int ExamCount, int Deviations);

/// <summary>A measured indicator with its reference (healthy adult) range and the range accepted on input.</summary>
public sealed record Indicator(int Id, string Code, string Group, string Name, string Unit, decimal RefMin, decimal RefMax,
    decimal ValidMin, decimal ValidMax, int Decimals, int SortOrder, bool IsActive)
{
    public string Title => Name + (Unit.Length > 0 ? ", " + Unit : "");
    // Числа всегда по-русски — с запятой, независимо от региональных настроек Windows.
    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
    public string Format(decimal value) => value.ToString("F" + Decimals, Ru);
    public string Format(double value) => value.ToString("F" + Decimals, Ru);
    public string RangeText => Format(RefMin) + "–" + Format(RefMax);
}

public sealed record IndicatorInput(string Code, string Group, string Name, string Unit, decimal RefMin, decimal RefMax,
    decimal ValidMin, decimal ValidMax, int Decimals, int SortOrder, bool IsActive);

public sealed record MeasuredValue(int IndicatorId, decimal Value);

public sealed record Examination(int Id, int PatientId, int DoctorId, string DoctorName, DateTime TakenAt, string? Comment, IReadOnlyList<MeasuredValue> Values);

public sealed record ExaminationInput(int PatientId, DateTime TakenAt, string? Comment, IReadOnlyList<MeasuredValue> Values);

/// <summary>A single value of an indicator for statistics and charts.</summary>
public sealed record SamplePoint(int PatientId, int IndicatorId, DateTime TakenAt, decimal Value);

public sealed record UserInput(string Login, string FullName, Role Role, bool IsActive, string? Password);

public static class Ages
{
    public static int Years(DateOnly birth, DateOnly day)
    {
        var years = day.Year - birth.Year;
        if (day < birth.AddYears(years)) years--;
        return Math.Max(0, years);
    }

    /// <summary>«35 лет», «41 год», «22 года».</summary>
    public static string Text(int years)
    {
        var n = years % 100;
        var word = n is >= 11 and <= 14 ? "лет" : (n % 10) switch { 1 => "год", 2 or 3 or 4 => "года", _ => "лет" };
        return years + " " + word;
    }
}
