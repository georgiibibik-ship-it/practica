using Microsoft.Data.SqlClient;
using MedTest.Core;
using MedTest.Data;

// Первичная настройка базы МедТест. Пароли передаются только через переменные окружения,
// чтобы они не попадали в историю команд и список процессов.
//   MedTest.Setup [--server (localdb)\MSSQLLocalDB] [--database MedTest_Dev] [--status] [--sample]
//   MEDTEST_ADMIN_LOGIN / MEDTEST_ADMIN_PASSWORD / MEDTEST_ADMIN_NAME — первый администратор
//   MEDTEST_DOCTOR_LOGIN / MEDTEST_DOCTOR_PASSWORD / MEDTEST_DOCTOR_NAME — первый врач
Console.OutputEncoding = System.Text.Encoding.UTF8;
string Arg(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
var server = Arg("--server", DatabaseSetup.DefaultServer);
var database = Arg("--database", DatabaseSetup.WorkingDatabase);
// MEDTEST_SERVER_CONNECTION — another SQL Server (connection string without a database), used by the checks.
var other = Environment.GetEnvironmentVariable("MEDTEST_SERVER_CONNECTION");
var cs = string.IsNullOrWhiteSpace(other) ? DatabaseSetup.ConnectionString(server, database)
    : new SqlConnectionStringBuilder(other) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;
try
{
    await DatabaseSetup.EnsureAsync(cs);
    Console.WriteLine("База готова: " + database);
    await using (var c = new SqlConnection(cs))
    {
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Role, COUNT(*) FROM dbo.Users WHERE IsActive = 1 GROUP BY Role", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var roles = new Dictionary<string, int>();
        while (await r.ReadAsync()) roles[r.GetString(0)] = r.GetInt32(1);
        if (args.Contains("--status"))
        {
            Console.WriteLine(roles.ContainsKey("Admin") ? "ADMIN_EXISTS" : "ADMIN_MISSING");
            Console.WriteLine(roles.ContainsKey("Doctor") ? "DOCTOR_EXISTS" : "DOCTOR_MISSING");
            return 0;
        }
    }
    foreach (var (role, prefix, title) in new[] { (Role.Admin, "ADMIN", "Администратор"), (Role.Doctor, "DOCTOR", "Врач") })
    {
        var login = Environment.GetEnvironmentVariable("MEDTEST_" + prefix + "_LOGIN");
        var password = Environment.GetEnvironmentVariable("MEDTEST_" + prefix + "_PASSWORD");
        if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password)) continue;
        var name = Environment.GetEnvironmentVariable("MEDTEST_" + prefix + "_NAME") ?? login;
        Console.WriteLine(await DatabaseSetup.CreateFirstUserAsync(cs, role, login, name, password)
            ? title + " создан. Логин: " + login : title + " уже есть, учётная запись не изменена.");
    }
    if (args.Contains("--sample"))
    {
        var doctor = Environment.GetEnvironmentVariable("MEDTEST_DOCTOR_LOGIN");
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 Login FROM dbo.Users WHERE Role='Doctor' AND IsActive=1 AND (@l IS NULL OR Login=@l) ORDER BY Id", c);
        cmd.Parameters.AddWithValue("@l", (object?)doctor ?? DBNull.Value);
        var login = await cmd.ExecuteScalarAsync() as string;
        if (login == null) { Console.WriteLine("Примеры не добавлены: нет ни одного врача."); return 1; }
        int added = await SampleData.FillAsync(cs, login);
        Console.WriteLine(added > 0 ? $"Добавлено пациентов с обследованиями: {added}." : "Пациенты уже есть, примеры не добавлялись.");
    }
    return 0;
}
catch (DomainException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (SqlException ex) { Console.Error.WriteLine("Не удалось подключиться к SQL Server LocalDB: " + ex.Message); return 3; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
