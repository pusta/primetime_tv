namespace PrimeTime.Server.Data;

/// <summary>Simple key/value settings table for small server-wide values.</summary>
public sealed class SettingsStore(Database database)
{
    private const string DeviceIdKey = "device_id";
    private const string SetupCompleteKey = "setup_complete";
    private readonly Lock _gate = new();

    public string? Get(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $now)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// True once the first library scan has finished. It never goes back to false, so the
    /// first-run setup only ever happens once, even if Jellyfin is disconnected later.
    /// </summary>
    public bool IsSetupComplete() => Get(SetupCompleteKey) == "true";

    public void MarkSetupComplete() => Set(SetupCompleteKey, "true");

    /// <summary>
    /// A stable ID for this PrimeTime install. Jellyfin ties sessions to a device ID,
    /// so reusing it means re-authenticating replaces our old session instead of adding one.
    /// </summary>
    public string GetOrCreateDeviceId()
    {
        lock (_gate)
        {
            var existing = Get(DeviceIdKey);
            if (existing is not null)
            {
                return existing;
            }

            var created = "primetime-" + Guid.NewGuid().ToString("N");
            Set(DeviceIdKey, created);
            return created;
        }
    }
}
