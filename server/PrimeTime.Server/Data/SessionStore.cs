using System.Security.Cryptography;

namespace PrimeTime.Server.Data;

/// <summary>
/// Sign-ins the server knows about. The cookie only names a session here,
/// so deleting the row ends the session even if someone kept a copy of the cookie.
/// </summary>
public sealed class SessionStore(Database database)
{
    public const string ClaimType = "primetime:session";

    // Longer than the cookie's lifetime, so a row can't be removed while its cookie still works.
    private const long KeepSeconds = 8 * 24 * 3600;

    // Most requests only read; last-seen doesn't need to be exact.
    private const long TouchSeconds = 3600;

    public string Create(long adminId)
    {
        var id = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var now = Now();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = "DELETE FROM sessions WHERE last_seen_ts < $cutoff;";
            prune.Parameters.AddWithValue("$cutoff", now - KeepSeconds);
            prune.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO sessions (id, admin_id, last_seen_ts) VALUES ($id, $admin, $now);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$admin", adminId);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return id;
    }

    public bool IsActive(string id)
    {
        using var connection = database.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT last_seen_ts FROM sessions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteScalar() is not long lastSeen)
            {
                return false;
            }

            if (Now() - lastSeen < TouchSeconds)
            {
                return true;
            }
        }

        using var touch = connection.CreateCommand();
        touch.CommandText = "UPDATE sessions SET last_seen_ts = $now WHERE id = $id;";
        touch.Parameters.AddWithValue("$id", id);
        touch.Parameters.AddWithValue("$now", Now());
        touch.ExecuteNonQuery();
        return true;
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Ends every session for this admin except the one given.</summary>
    public void DeleteOthers(long adminId, string keepId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE admin_id = $admin AND id <> $keep;";
        command.Parameters.AddWithValue("$admin", adminId);
        command.Parameters.AddWithValue("$keep", keepId);
        command.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
