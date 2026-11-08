using PrimeTime.Server.Security;

namespace PrimeTime.Server.Data;

public sealed record AdminUser(long Id, string Username);

public sealed class AdminUserStore(Database database)
{
    public bool AnyExist()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM admin_users);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// Creates the first admin. Returns null when an admin already exists, so two
    /// concurrent setup requests can't both succeed.
    /// </summary>
    public AdminUser? CreateFirstAdmin(string username, string password)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO admin_users (username, password_hash, created_at)
            SELECT $username, $hash, $now
            WHERE NOT EXISTS (SELECT 1 FROM admin_users)
            RETURNING id, username;
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$hash", PasswordHashing.Hash(password));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? new AdminUser(reader.GetInt64(0), reader.GetString(1)) : null;
    }

    public AdminUser? VerifyCredentials(string username, string password)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, username, password_hash FROM admin_users WHERE username = $username;";
        command.Parameters.AddWithValue("$username", username);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Spend the same time as a real check so usernames can't be probed by timing.
            PasswordHashing.Verify(password, PasswordHashing.DummyHash);
            return null;
        }

        return PasswordHashing.Verify(password, reader.GetString(2))
            ? new AdminUser(reader.GetInt64(0), reader.GetString(1))
            : null;
    }
}
