using Microsoft.AspNetCore.DataProtection;

namespace PrimeTime.Server.Data;

public static class JellyfinAuthMethod
{
    public const string ApiKey = "apiKey";
    public const string Password = "password";
}

/// <summary>The saved Jellyfin connection. <see cref="Token"/> is decrypted in memory only.</summary>
public sealed record SavedJellyfinConnection(
    string BaseUrl,
    string AuthMethod,
    string? Username,
    string? UserId,
    string Token,
    string? ServerId,
    string? ServerName,
    string? ServerVersion,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persists the Jellyfin connection. The API key or access token is encrypted with
/// ASP.NET Core Data Protection before it reaches the database; a Jellyfin password
/// is never stored at all (we exchange it for an access token).
/// </summary>
public sealed class JellyfinConnectionStore(Database database, IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtector _protector =
        protectionProvider.CreateProtector("PrimeTime.Jellyfin.Token.v1");

    public bool Exists()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM jellyfin_connection WHERE id = 1);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public SavedJellyfinConnection? Get()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT base_url, auth_method, username, user_id, token_protected,
                   server_id, server_name, server_version, updated_at
            FROM jellyfin_connection WHERE id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SavedJellyfinConnection(
            BaseUrl: reader.GetString(0),
            AuthMethod: reader.GetString(1),
            Username: reader.IsDBNull(2) ? null : reader.GetString(2),
            UserId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Token: _protector.Unprotect(reader.GetString(4)),
            ServerId: reader.IsDBNull(5) ? null : reader.GetString(5),
            ServerName: reader.IsDBNull(6) ? null : reader.GetString(6),
            ServerVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(8)));
    }

    public void Save(SavedJellyfinConnection value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jellyfin_connection
                (id, base_url, auth_method, username, user_id, token_protected,
                 server_id, server_name, server_version, updated_at)
            VALUES (1, $baseUrl, $authMethod, $username, $userId, $token,
                    $serverId, $serverName, $serverVersion, $updatedAt)
            ON CONFLICT (id) DO UPDATE SET
                base_url = excluded.base_url,
                auth_method = excluded.auth_method,
                username = excluded.username,
                user_id = excluded.user_id,
                token_protected = excluded.token_protected,
                server_id = excluded.server_id,
                server_name = excluded.server_name,
                server_version = excluded.server_version,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$baseUrl", value.BaseUrl);
        command.Parameters.AddWithValue("$authMethod", value.AuthMethod);
        command.Parameters.AddWithValue("$username", (object?)value.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$userId", (object?)value.UserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$token", _protector.Protect(value.Token));
        command.Parameters.AddWithValue("$serverId", (object?)value.ServerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$serverName", (object?)value.ServerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$serverVersion", (object?)value.ServerVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", value.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Delete()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jellyfin_connection WHERE id = 1;";
        command.ExecuteNonQuery();
    }
}
