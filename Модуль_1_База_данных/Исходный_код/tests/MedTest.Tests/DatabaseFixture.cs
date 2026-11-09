using Microsoft.Data.SqlClient;
using MedTest.Core;
using MedTest.Data;

namespace MedTest.Tests;

/// <summary>
/// A fresh test database MedTest_Test_* for the data tests, deleted afterwards.
/// Server: MEDTEST_TEST_SERVER (a connection string without a database), otherwise LocalDB.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = "";
    public MedDatabase Db { get; private set; } = null!;
    public AppUser Admin { get; private set; } = null!;
    public AppUser Doctor { get; private set; } = null!;
    public AppUser OtherDoctor { get; private set; } = null!;
    public const string Password = "Проверка-2026!";

    public static string ServerConnection(string database)
    {
        var server = Environment.GetEnvironmentVariable("MEDTEST_TEST_SERVER");
        var b = string.IsNullOrWhiteSpace(server)
            ? new SqlConnectionStringBuilder(DatabaseSetup.ConnectionString(DatabaseSetup.DefaultServer, database))
            : new SqlConnectionStringBuilder(server) { InitialCatalog = database };
        b.TrustServerCertificate = true;
        return b.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        ConnectionString = ServerConnection("MedTest_Test_" + Guid.NewGuid().ToString("N"));
        await DatabaseSetup.EnsureAsync(ConnectionString);
        await DatabaseSetup.CreateFirstUserAsync(ConnectionString, Role.Admin, "test_admin", "Администратор Проверки", Password);
        await DatabaseSetup.CreateFirstUserAsync(ConnectionString, Role.Doctor, "test_doctor", "Врач Первый", Password);
        Db = new MedDatabase(ConnectionString) { Now = () => new DateTime(2026, 11, 5, 12, 0, 0) };
        Admin = await Db.LoginAsync("test_admin", Password, Role.Admin);
        Doctor = await Db.LoginAsync("test_doctor", Password, Role.Doctor);
        OtherDoctor = await Db.SaveUserAsync(Admin, null, new UserInput("test_doctor2", "Врач Второй", Role.Doctor, true, Password));
    }

    public async Task DisposeAsync() => await DatabaseSetup.DropTestDatabaseAsync(ConnectionString);
}

[CollectionDefinition("db")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
