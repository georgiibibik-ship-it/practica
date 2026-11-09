using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

/// <summary>An expected refusal: wrong input, missing rights, absent record or a stale screen. The message is shown to the user as is.</summary>
public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public static DomainException Validation(string message) => new("validation", message);
    public static DomainException Forbidden(string message = "Недостаточно прав для этого действия.") => new("forbidden", message);
    public static DomainException NotFound(string message) => new("not_found", message);
}

internal static class Sql
{
    public static SqlCommand Command(SqlConnection c, SqlTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 30 };
        foreach (var (name, value) in args)
        {
            var p = cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            switch (value)
            {
                case DateTime: p.SqlDbType = SqlDbType.DateTime2; break;
                case DateOnly d: p.SqlDbType = SqlDbType.Date; p.Value = d.ToDateTime(TimeOnly.MinValue); break;
                case decimal: p.SqlDbType = SqlDbType.Decimal; p.Precision = 12; p.Scale = 3; break;
                case string s: p.Size = Math.Max(1, s.Length); break;
            }
        }
        return cmd;
    }

    public static async Task<int> RunAsync(SqlConnection c, SqlTransaction? tx, string sql, params (string, object?)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(SqlConnection c, SqlTransaction? tx, string sql, params (string, object?)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public static async Task<List<T>> ListAsync<T>(SqlConnection c, SqlTransaction? tx, string sql, Func<SqlDataReader, T> map, params (string, object?)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }

    public static string? NullableString(this SqlDataReader r, string name) => r[name] is DBNull ? null : (string)r[name];
    public static DateOnly Date(this SqlDataReader r, string name) => DateOnly.FromDateTime((DateTime)r[name]);
}

internal static class Passwords
{
    public const int Iterations = 210000;
    public static byte[] Hash(string password, byte[] salt, int iterations = Iterations)
    {
        try { return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32); }
        // Some Windows compatibility layers (Wine) lack the system PBKDF2; the same function is then computed here.
        catch (CryptographicException) { return Pbkdf2Sha256(System.Text.Encoding.UTF8.GetBytes(password), salt, iterations); }
    }

    /// <summary>PBKDF2-HMAC-SHA256 (RFC 8018), one 32-byte block. Gives the same bytes as the system implementation.</summary>
    internal static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations)
    {
        using var hmac = new HMACSHA256(password);
        var block = new byte[salt.Length + 4];
        salt.CopyTo(block, 0);
        block[^1] = 1; // block index 1, big-endian
        var u = hmac.ComputeHash(block);
        var result = (byte[])u.Clone();
        for (int i = 1; i < iterations; i++)
        {
            u = hmac.ComputeHash(u);
            for (int k = 0; k < result.Length; k++) result[k] ^= u[k];
        }
        return result;
    }
    public static byte[] Salt() => RandomNumberGenerator.GetBytes(32);
    public static bool Matches(string password, byte[] salt, int iterations, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(Hash(password, salt, iterations), expected);
}

/// <summary>Input rules shared by the data layer and the forms.</summary>
public static partial class Rules
{
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{3,32}\z")] private static partial Regex LoginPattern();
    [GeneratedRegex(@"\A[А-ЯЁа-яёA-Za-z][А-ЯЁа-яёA-Za-z' \-]{0,59}\z")] private static partial Regex NamePattern();
    [GeneratedRegex(@"\A\+?[0-9]{10,15}\z")] private static partial Regex PhonePattern();
    [GeneratedRegex(@"\A[A-Z][A-Z0-9_]{0,15}\z")] private static partial Regex CodePattern();

    public static string Login(string? login)
    {
        login = (login ?? "").Trim();
        if (!LoginPattern().IsMatch(login)) throw DomainException.Validation("Логин: 3–32 латинские буквы, цифры, точка, дефис или подчёркивание.");
        return login;
    }

    public static void Password(string password)
    {
        if (password.Length is < 8 or > 128) throw DomainException.Validation("Пароль должен содержать от 8 до 128 символов.");
    }

    public static string Name(string? value, string field, bool required = true)
    {
        var v = string.Join(' ', (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (v.Length == 0)
        {
            if (required) throw DomainException.Validation("Заполните поле «" + field + "».");
            return "";
        }
        if (!NamePattern().IsMatch(v)) throw DomainException.Validation("«" + field + "»: только буквы, пробел, дефис или апостроф, до 60 символов.");
        return char.ToUpper(v[0]) + v[1..];
    }

    /// <summary>Phone as digits with an optional leading plus; spaces, brackets and dashes are dropped. Empty means no phone.</summary>
    public static string? Phone(string? value)
    {
        var v = new string((value ?? "").Where(ch => char.IsDigit(ch) || ch == '+').ToArray());
        if (v.Length == 0) return null;
        if (v.StartsWith('8') && v.Length == 11) v = "+7" + v[1..];
        if (!PhonePattern().IsMatch(v)) throw DomainException.Validation("Телефон: 10–15 цифр, например +7 900 123-45-67.");
        return v;
    }

    public static string? Text(string? value, string field, int max)
    {
        var v = (value ?? "").Trim();
        if (v.Length > max) throw DomainException.Validation("«" + field + "»: не больше " + max + " символов.");
        return v.Length == 0 ? null : v;
    }

    public static PatientInput Patient(PatientInput p, DateOnly today)
    {
        var last = Name(p.LastName, "Фамилия");
        var first = Name(p.FirstName, "Имя");
        var middle = Name(p.MiddleName, "Отчество", required: false);
        if (p.BirthDate < new DateOnly(1900, 1, 1) || p.BirthDate > today) throw DomainException.Validation("Дата рождения должна быть не раньше 01.01.1900 и не позже сегодняшней.");
        return new(last, first, middle.Length == 0 ? null : middle, p.BirthDate, p.Sex, Phone(p.Phone), Text(p.Note, "Примечание", 1000));
    }

    public static IndicatorInput Indicator(IndicatorInput i)
    {
        var code = (i.Code ?? "").Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(code)) throw DomainException.Validation("Код: латинские буквы и цифры, начинается с буквы, до 16 символов, например GLU.");
        var group = Text(i.Group, "Группа", 60) ?? throw DomainException.Validation("Заполните поле «Группа».");
        var name = Text(i.Name, "Название", 100) ?? throw DomainException.Validation("Заполните поле «Название».");
        var unit = Text(i.Unit, "Единица", 30) ?? "";
        if (i.Decimals is < 0 or > 3) throw DomainException.Validation("Точность: от 0 до 3 знаков после запятой.");
        if (i.RefMin > i.RefMax) throw DomainException.Validation("Нижняя граница нормы больше верхней.");
        if (i.ValidMin >= i.ValidMax) throw DomainException.Validation("Допустимый диапазон ввода задан неверно: минимум должен быть меньше максимума.");
        if (i.ValidMin > i.RefMin || i.ValidMax < i.RefMax) throw DomainException.Validation("Норма должна лежать внутри допустимого диапазона ввода.");
        return i with { Code = code, Group = group, Name = name, Unit = unit };
    }

    /// <summary>Ends a phrase with a full stop unless it already ends with one (units like «мм рт. ст.»).</summary>
    public static string Sentence(string text) => text.TrimEnd().EndsWith('.') ? text.TrimEnd() : text.TrimEnd() + ".";

    /// <summary>Checks one typed value against its indicator; returns it rounded to the indicator precision.</summary>
    public static decimal Value(decimal value, Indicator indicator)
    {
        if (value < indicator.ValidMin || value > indicator.ValidMax)
            throw DomainException.Validation(Sentence($"«{indicator.Name}»: значение {indicator.Format(value)} вне допустимого диапазона {indicator.Format(indicator.ValidMin)}–{indicator.Format(indicator.ValidMax)} {indicator.Unit}") + " Проверьте ввод.");
        return Math.Round(value, indicator.Decimals, MidpointRounding.AwayFromZero);
    }
}
