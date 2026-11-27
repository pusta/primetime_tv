namespace PrimeTime.Server.Data;

/// <summary>
/// One stream, from tuning in to stopping. <paramref name="EndedTs"/> is null while it plays, and stays null
/// if PrimeTime was stopped first.
/// </summary>
public sealed record StreamRecord(
    long Id,
    long StartedTs,
    long? EndedTs,
    string? EndReason,
    long ChannelId,
    int ChannelNumber,
    string ChannelName,
    string Title,
    string? Subtitle,
    string Viewer,
    string Client,
    string? Address,
    string Delivery,
    string? Video,
    long BytesSent);

public static class StreamEndReason
{
    public const string Stopped = "stopped";
    public const string Idle = "idle";
    public const string StoppedByAdmin = "admin";
}

/// <summary>A log of streams, so Settings can show what was watched recently. Keeps the newest few hundred.</summary>
public sealed class StreamHistoryStore(Database database)
{
    private const int Kept = 200;

    public long Start(StreamRecord stream)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO stream_history
                (started_ts, channel_id, channel_number, channel_name, title, subtitle,
                 viewer, client, address, delivery, video)
            VALUES ($started, $channelId, $number, $name, $title, $subtitle,
                    $viewer, $client, $address, $delivery, $video)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$started", stream.StartedTs);
        command.Parameters.AddWithValue("$channelId", stream.ChannelId);
        command.Parameters.AddWithValue("$number", stream.ChannelNumber);
        command.Parameters.AddWithValue("$name", stream.ChannelName);
        command.Parameters.AddWithValue("$title", stream.Title);
        command.Parameters.AddWithValue("$subtitle", (object?)stream.Subtitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$viewer", stream.Viewer);
        command.Parameters.AddWithValue("$client", stream.Client);
        command.Parameters.AddWithValue("$address", (object?)stream.Address ?? DBNull.Value);
        command.Parameters.AddWithValue("$delivery", stream.Delivery);
        command.Parameters.AddWithValue("$video", (object?)stream.Video ?? DBNull.Value);
        var id = Convert.ToInt64(command.ExecuteScalar());

        using var trim = connection.CreateCommand();
        trim.Transaction = tx;
        trim.CommandText = """
            DELETE FROM stream_history
            WHERE id NOT IN (SELECT id FROM stream_history ORDER BY id DESC LIMIT $kept);
            """;
        trim.Parameters.AddWithValue("$kept", Kept);
        trim.ExecuteNonQuery();
        tx.Commit();
        return id;
    }

    public void End(long id, long endedTs, string reason, string delivery, long bytesSent)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE stream_history
            SET ended_ts = $ended, end_reason = $reason, delivery = $delivery, bytes_sent = $bytes
            WHERE id = $id AND ended_ts IS NULL;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ended", endedTs);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$delivery", delivery);
        command.Parameters.AddWithValue("$bytes", bytesSent);
        command.ExecuteNonQuery();
    }

    /// <summary>The newest streams that aren't playing now.</summary>
    public List<StreamRecord> Recent(int limit, IReadOnlyCollection<long> playing)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_ts, ended_ts, end_reason, channel_id, channel_number, channel_name, title, subtitle,
                   viewer, client, address, delivery, video, bytes_sent
            FROM stream_history
            WHERE id NOT IN (SELECT value FROM json_each($playing))
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$playing", System.Text.Json.JsonSerializer.Serialize(playing));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var streams = new List<StreamRecord>();
        while (reader.Read())
        {
            streams.Add(new StreamRecord(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetInt64(14)));
        }

        return streams;
    }
}
