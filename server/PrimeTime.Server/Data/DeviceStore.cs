using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PrimeTime.Server.Data;

public sealed record DeviceLinkStart(string DeviceCode, string UserCode, long ExpiresAt);

public sealed record PendingDevice(string Name, string? Platform, long ExpiresAt);

public enum DevicePollStatus { Pending, SlowDown, Approved, Denied, Expired, Unknown }

public sealed record DevicePoll(DevicePollStatus Status, string? AccessToken = null);

public sealed record LinkedDevice(long Id, long AdminId, string Name, string? Platform, long CreatedAt, long LastSeenAt);

/// <summary>
/// Apps signing in by QR code. An app starts a link, the admin approves its short code on the
/// website, and the app swaps its secret device code for a token. Only hashes of secrets are stored.
/// </summary>
public sealed class DeviceStore(Database database)
{
    public const int PollIntervalSeconds = 5;

    private const int LinkLifetimeSeconds = 10 * 60;
    private const int MaxPendingLinks = 100;
    private const long TouchSeconds = 3600;

    // No vowels (so codes can't spell words) and no characters that look alike.
    private const string CodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    /// <summary>Returns null if too many links are already waiting.</summary>
    public DeviceLinkStart? StartLink(string name, string? platform)
    {
        var now = Now();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, "DELETE FROM device_links WHERE expires_ts <= $now;", ("$now", now));
        if (Scalar(connection, tx, "SELECT COUNT(*) FROM device_links;") >= MaxPendingLinks)
        {
            return null;
        }

        var deviceCode = RandomNumberGenerator.GetHexString(64, lowercase: true);
        var userCode = NewUserCode();
        var expiresAt = now + LinkLifetimeSeconds;
        Execute(connection, tx, """
            INSERT INTO device_links (device_code_hash, user_code, name, platform, status, expires_ts, last_poll_ts)
            VALUES ($hash, $code, $name, $platform, 'pending', $expires, 0);
            """,
            ("$hash", Hash(deviceCode)), ("$code", userCode), ("$name", name),
            ("$platform", (object?)platform ?? DBNull.Value), ("$expires", expiresAt));
        tx.Commit();
        return new DeviceLinkStart(deviceCode, FormatCode(userCode), expiresAt);
    }

    public PendingDevice? FindPending(string userCode)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, platform, expires_ts FROM device_links
            WHERE user_code = $code AND status = 'pending' AND expires_ts > $now;
            """;
        command.Parameters.AddWithValue("$code", NormalizeCode(userCode));
        command.Parameters.AddWithValue("$now", Now());
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new PendingDevice(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    public bool Approve(string userCode, long adminId) => Decide(userCode, "approved", adminId);

    public bool Deny(string userCode) => Decide(userCode, "denied", null);

    private bool Decide(string userCode, string status, long? adminId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE device_links SET status = $status, admin_id = $admin
            WHERE user_code = $code AND status = 'pending' AND expires_ts > $now;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$admin", (object?)adminId ?? DBNull.Value);
        command.Parameters.AddWithValue("$code", NormalizeCode(userCode));
        command.Parameters.AddWithValue("$now", Now());
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>The app's regular check. An approved link gives out its token once and is then gone.</summary>
    public DevicePoll Poll(string deviceCode)
    {
        var hash = Hash(deviceCode);
        var now = Now();
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        string status, name;
        string? platform;
        long expiresAt, lastPoll;
        long? adminId;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT status, name, platform, expires_ts, last_poll_ts, admin_id
                FROM device_links WHERE device_code_hash = $hash;
                """;
            read.Parameters.AddWithValue("$hash", hash);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return new DevicePoll(DevicePollStatus.Unknown);
            }

            status = reader.GetString(0);
            name = reader.GetString(1);
            platform = reader.IsDBNull(2) ? null : reader.GetString(2);
            expiresAt = reader.GetInt64(3);
            lastPoll = reader.GetInt64(4);
            adminId = reader.IsDBNull(5) ? null : reader.GetInt64(5);
        }

        const string remove = "DELETE FROM device_links WHERE device_code_hash = $hash;";
        DevicePoll result;
        if (expiresAt <= now)
        {
            Execute(connection, tx, remove, ("$hash", hash));
            result = new DevicePoll(DevicePollStatus.Expired);
        }
        else if (status == "denied")
        {
            Execute(connection, tx, remove, ("$hash", hash));
            result = new DevicePoll(DevicePollStatus.Denied);
        }
        else if (status == "approved" && adminId is { } admin)
        {
            Execute(connection, tx, remove, ("$hash", hash));
            var token = "ptd_" + RandomNumberGenerator.GetHexString(64, lowercase: true);
            Execute(connection, tx, """
                INSERT INTO device_tokens (admin_id, name, platform, token_hash, created_ts, last_seen_ts)
                VALUES ($admin, $name, $platform, $hash, $now, $now);
                """,
                ("$admin", admin), ("$name", name), ("$platform", (object?)platform ?? DBNull.Value),
                ("$hash", Hash(token)), ("$now", now));
            result = new DevicePoll(DevicePollStatus.Approved, token);
        }
        else
        {
            var tooSoon = now - lastPoll < PollIntervalSeconds - 1;
            Execute(connection, tx, "UPDATE device_links SET last_poll_ts = $now WHERE device_code_hash = $hash;",
                ("$now", now), ("$hash", hash));
            result = new DevicePoll(tooSoon ? DevicePollStatus.SlowDown : DevicePollStatus.Pending);
        }

        tx.Commit();
        return result;
    }

    public LinkedDevice? FindByToken(string token)
    {
        var now = Now();
        using var connection = database.Open();
        LinkedDevice device;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT id, admin_id, name, platform, created_ts, last_seen_ts
                FROM device_tokens WHERE token_hash = $hash;
                """;
            read.Parameters.AddWithValue("$hash", Hash(token));
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            device = ReadDevice(reader);
        }

        if (now - device.LastSeenAt >= TouchSeconds)
        {
            Execute(connection, null, "UPDATE device_tokens SET last_seen_ts = $now WHERE id = $id;",
                ("$now", now), ("$id", device.Id));
        }

        return device;
    }

    public IReadOnlyList<LinkedDevice> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, admin_id, name, platform, created_ts, last_seen_ts
            FROM device_tokens ORDER BY last_seen_ts DESC;
            """;
        var result = new List<LinkedDevice>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadDevice(reader));
        }

        return result;
    }

    public bool Revoke(long id)
    {
        using var connection = database.Open();
        return Execute(connection, null, "DELETE FROM device_tokens WHERE id = $id;", ("$id", id)) == 1;
    }

    public void RevokeAll(long adminId)
    {
        using var connection = database.Open();
        Execute(connection, null, "DELETE FROM device_tokens WHERE admin_id = $admin;", ("$admin", adminId));
    }

    private static LinkedDevice ReadDevice(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5));

    private static string NewUserCode() => RandomNumberGenerator.GetString(CodeAlphabet, 8);

    private static string FormatCode(string code) => $"{code[..4]}-{code[4..]}";

    private static string NormalizeCode(string code) =>
        new(code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static int Execute(
        SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
