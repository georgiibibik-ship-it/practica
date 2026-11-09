using Microsoft.Data.SqlClient;
using MedTest.Core;

namespace MedTest.Data;

/// <summary>
/// All reads and changes of the application. Every change checks the role and the active state of the acting user
/// in the database itself, so the rules hold even if a button is shown by mistake.
/// </summary>
public sealed partial class MedDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    /// <summary>Local clinic time; examinations are stored and shown in it.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    DateOnly Today => DateOnly.FromDateTime(Now());

    async Task<SqlConnection> OpenAsync()
    {
        var c = new SqlConnection(ConnectionString);
        try { await c.OpenAsync(); }
        catch (SqlException ex)
        {
            await c.DisposeAsync();
            throw new DomainException("unavailable", "База данных недоступна. Запустите «Первый запуск» или проверьте LocalDB. " + ex.Message);
        }
        return c;
    }

    /// <summary>Re-reads the acting user: blocked or deleted accounts cannot change anything.</summary>
    static async Task Require(SqlConnection c, SqlTransaction? tx, AppUser actor, params Role[] roles)
    {
        var row = await Sql.ListAsync(c, tx, "SELECT Role, IsActive FROM dbo.Users WHERE Id=@id", r => (Role: (string)r["Role"], Active: (bool)r["IsActive"]), ("@id", actor.Id));
        if (row.Count == 0 || !row[0].Active) throw DomainException.Forbidden("Учётная запись заблокирована. Войдите снова.");
        if (!roles.Contains(Roles.Parse(row[0].Role))) throw DomainException.Forbidden();
    }

    static Task Audit(SqlConnection c, SqlTransaction? tx, AppUser actor, string action, string entity, int? id, string? details = null) =>
        Sql.RunAsync(c, tx, "INSERT dbo.AuditLog(UserId,Action,Entity,EntityId,Details) VALUES(@u,@a,@e,@id,@d)",
            ("@u", actor.Id), ("@a", action), ("@e", entity), ("@id", id), ("@d", details is { Length: > 1000 } ? details[..1000] : details));

    // ---------------- Вход и сотрудники ----------------

    /// <summary>Checks the password and that the account has the role chosen on the login screen.</summary>
    public async Task<AppUser> LoginAsync(string login, string password, Role mode)
    {
        await using var c = await OpenAsync();
        var rows = await Sql.ListAsync(c, null, "SELECT Id,Login,FullName,Role,IsActive,PasswordHash,Salt,Iterations FROM dbo.Users WHERE Login=@l",
            r => (User: MapUser(r), Hash: (byte[])r["PasswordHash"], Salt: (byte[])r["Salt"], Iterations: (int)r["Iterations"]), ("@l", (login ?? "").Trim()));
        // Same answer for an unknown login and a wrong password.
        if (rows.Count == 0 || !Passwords.Matches(password ?? "", rows[0].Salt, rows[0].Iterations, rows[0].Hash))
            throw new DomainException("auth", "Неверный логин или пароль.");
        var user = rows[0].User;
        if (!user.IsActive) throw new DomainException("auth", "Учётная запись заблокирована. Обратитесь к администратору.");
        if (user.Role != mode) throw new DomainException("auth", "Эта учётная запись — " + Roles.Title(user.Role).ToLowerInvariant() + ". Выберите режим «" + Roles.Title(user.Role) + "».");
        await Audit(c, null, user, "login", "User", user.Id);
        return user;
    }

    static AppUser MapUser(SqlDataReader r) => new((int)r["Id"], (string)r["Login"], (string)r["FullName"], Roles.Parse((string)r["Role"]), (bool)r["IsActive"]);

    public async Task<List<AppUser>> ListUsersAsync(AppUser actor)
    {
        await using var c = await OpenAsync();
        await Require(c, null, actor, Role.Admin);
        return await Sql.ListAsync(c, null, "SELECT Id,Login,FullName,Role,IsActive FROM dbo.Users ORDER BY Role, FullName", MapUser);
    }

    /// <summary>Creates (id null, password required) or changes an account. An empty password keeps the old one.</summary>
    public async Task<AppUser> SaveUserAsync(AppUser actor, int? id, UserInput input)
    {
        var login = Rules.Login(input.Login);
        var name = Rules.Text(input.FullName, "ФИО", 150) ?? throw DomainException.Validation("Заполните поле «ФИО».");
        if (id == null || !string.IsNullOrEmpty(input.Password)) Rules.Password(input.Password ?? "");
        if (id == actor.Id && (!input.IsActive || input.Role != Role.Admin)) throw DomainException.Validation("Нельзя заблокировать себя или снять с себя роль администратора.");
        await using var c = await OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        await Require(c, tx, actor, Role.Admin);
        if (Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.Users WHERE Login=@l AND Id<>ISNULL(@id,0)", ("@l", login), ("@id", id))) > 0)
            throw DomainException.Validation("Логин «" + login + "» уже занят.");
        int userId;
        if (id == null)
        {
            var salt = Passwords.Salt();
            userId = Convert.ToInt32(await Sql.ScalarAsync(c, tx, "INSERT dbo.Users(Login,FullName,Role,IsActive,PasswordHash,Salt,Iterations) OUTPUT inserted.Id VALUES(@l,@n,@r,@a,@h,@s,@i)",
                ("@l", login), ("@n", name), ("@r", Roles.Code(input.Role)), ("@a", input.IsActive), ("@h", Passwords.Hash(input.Password!, salt)), ("@s", salt), ("@i", Passwords.Iterations)));
            await Audit(c, tx, actor, "user.create", "User", userId, login);
        }
        else
        {
            userId = id.Value;
            int changed = await Sql.RunAsync(c, tx, "UPDATE dbo.Users SET Login=@l,FullName=@n,Role=@r,IsActive=@a WHERE Id=@id",
                ("@l", login), ("@n", name), ("@r", Roles.Code(input.Role)), ("@a", input.IsActive), ("@id", userId));
            if (changed == 0) throw DomainException.NotFound("Сотрудник не найден.");
            if (!string.IsNullOrEmpty(input.Password))
            {
                var salt = Passwords.Salt();
                await Sql.RunAsync(c, tx, "UPDATE dbo.Users SET PasswordHash=@h,Salt=@s,Iterations=@i WHERE Id=@id",
                    ("@h", Passwords.Hash(input.Password, salt)), ("@s", salt), ("@i", Passwords.Iterations), ("@id", userId));
            }
            if (Convert.ToInt32(await Sql.ScalarAsync(c, tx, "SELECT COUNT(*) FROM dbo.Users WHERE Role='Admin' AND IsActive=1")) == 0)
                throw DomainException.Validation("Должен остаться хотя бы один активный администратор.");
            await Audit(c, tx, actor, "user.update", "User", userId, login);
        }
        await tx.CommitAsync();
        return new(userId, login, name, input.Role, input.IsActive);
    }
}
