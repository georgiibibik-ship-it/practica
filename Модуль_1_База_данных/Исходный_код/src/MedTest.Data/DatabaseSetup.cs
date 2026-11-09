using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

/// <summary>Creates the database and applies the embedded migrations in order; safe to run again.</summary>
public static partial class DatabaseSetup
{
    public const string DefaultServer = @"(localdb)\MSSQLLocalDB";
    public const string WorkingDatabase = "MedTest_Dev";

    [GeneratedRegex(@"\AMedTest_[A-Za-z0-9_]{1,100}\z")] private static partial Regex NamePattern();

    public static string ConnectionString(string server, string database) => new SqlConnectionStringBuilder
    {
        DataSource = server, InitialCatalog = database, IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 15
    }.ConnectionString;

    /// <summary>Database name of a connection string, checked against the MedTest_ prefix.</summary>
    public static string DatabaseName(string connectionString)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!NamePattern().IsMatch(name)) throw new ArgumentException("Имя базы должно начинаться с MedTest_ и содержать только латинские буквы, цифры и подчёркивание: " + name);
        return name;
    }

    /// <summary>Applies new migrations to an existing database (called by the app on sign-in). A missing database is left alone:
    /// it is created by the first-run setup together with the first accounts.</summary>
    public static async Task<bool> UpgradeAsync(string connectionString)
    {
        var database = DatabaseName(connectionString);
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 15 }.ConnectionString;
        await using (var c = new SqlConnection(master))
        {
            await c.OpenAsync();
            if (await Sql.ScalarAsync(c, null, "SELECT DB_ID(@db)", ("@db", database)) is null or DBNull) return false;
        }
        await EnsureAsync(connectionString);
        return true;
    }

    public static async Task EnsureAsync(string connectionString)
    {
        var database = DatabaseName(connectionString);
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var c = new SqlConnection(master))
        {
            await c.OpenAsync();
            await Sql.RunAsync(c, null, $"IF DB_ID(@db) IS NULL CREATE DATABASE [{database}]", ("@db", database));
        }

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync();
        await Sql.RunAsync(db, tx, "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource='MedTest.Setup', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r < 0 THROW 51000, 'Setup lock timeout', 1;");
        var assembly = typeof(DatabaseSetup).Assembly;
        bool hasTable = Convert.ToInt32(await Sql.ScalarAsync(db, tx, "SELECT COUNT(*) FROM sys.tables WHERE name='SchemaMigrations' AND schema_id=SCHEMA_ID('dbo')")) > 0;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n, StringComparer.Ordinal))
        {
            var version = int.Parse(resource.Split('.')[^2]);
            if (hasTable && Convert.ToInt32(await Sql.ScalarAsync(db, tx, "SELECT COUNT(*) FROM dbo.SchemaMigrations WHERE Version=@v", ("@v", version))) > 0) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var script = await reader.ReadToEndAsync();
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                if (!string.IsNullOrWhiteSpace(batch)) await Sql.RunAsync(db, tx, batch);
            await Sql.RunAsync(db, tx, "INSERT dbo.SchemaMigrations(Version) VALUES(@v)", ("@v", version));
            hasTable = true;
        }
        await tx.CommitAsync();
    }

    /// <summary>Creates the first account of a role when there is no active one yet (first run). Returns false when one already exists.</summary>
    public static async Task<bool> CreateFirstUserAsync(string connectionString, Role role, string login, string fullName, string password)
    {
        login = Rules.Login(login); Rules.Password(password);
        var name = Rules.Text(fullName, "ФИО", 150) ?? login;
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        // Disabled accounts (for example, the doctor of the sample data script) cannot sign in, so they do not count.
        if (Convert.ToInt32(await Sql.ScalarAsync(c, null, "SELECT COUNT(*) FROM dbo.Users WHERE Role=@r AND IsActive=1", ("@r", Roles.Code(role)))) > 0) return false;
        var salt = Passwords.Salt();
        await Sql.RunAsync(c, null, "INSERT dbo.Users(Login,FullName,Role,PasswordHash,Salt,Iterations) VALUES(@l,@n,@r,@h,@s,@i)",
            ("@l", login), ("@n", name), ("@r", Roles.Code(role)), ("@h", Passwords.Hash(password, salt)), ("@s", salt), ("@i", Passwords.Iterations));
        return true;
    }

    /// <summary>Deletes a test database. Refuses any name outside MedTest_Test_.</summary>
    public static async Task DropTestDatabaseAsync(string connectionString)
    {
        var database = DatabaseName(connectionString);
        if (!database.StartsWith("MedTest_Test_", StringComparison.Ordinal)) throw new InvalidOperationException("Удалять можно только тестовые базы MedTest_Test_*.");
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var c = new SqlConnection(master);
        await c.OpenAsync();
        await Sql.RunAsync(c, null, $"IF DB_ID(@db) IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", ("@db", database));
    }
}
