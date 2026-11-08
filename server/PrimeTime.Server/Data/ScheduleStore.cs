namespace PrimeTime.Server.Data;

public static class ScheduleItemType
{
    public const string Movie = "movie";
    public const string Episode = "episode";
}

public sealed record ScheduleEntry(
    long ChannelId,
    string ItemType,
    string ItemId,
    string? ShowId,
    int? Season,
    int? Episode,
    string Title,
    string? Subtitle,
    int Cycle,
    long StartTs,
    long EndTs);

/// <summary>
/// The stored timeline. It's also the channels' memory: what has played in the current pass
/// through a channel's content comes from these rows, so deleting future rows rewinds it correctly.
/// </summary>
public sealed class ScheduleStore(Database database)
{
    private const long HistorySeconds = 14 * 24 * 3600;

    public long? LastEndTs(long channelId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(end_ts) FROM schedule_entries WHERE channel_id = $id;";
        command.Parameters.AddWithValue("$id", channelId);
        return command.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>A channel's current pass through its content, what that has played, and the last item aired.</summary>
    public (int Cycle, HashSet<string> Played, string? LastItemId) CurrentCycle(long channelId)
    {
        using var connection = database.Open();
        var cycle = 1;
        string? last = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cycle, item_id FROM schedule_entries
                WHERE channel_id = $id
                ORDER BY start_ts DESC LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", channelId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                cycle = reader.GetInt32(0);
                last = reader.GetString(1);
            }
        }

        var played = new HashSet<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item_id FROM schedule_entries
                WHERE channel_id = $id AND cycle = $cycle;
                """;
            command.Parameters.AddWithValue("$id", channelId);
            command.Parameters.AddWithValue("$cycle", cycle);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                played.Add(reader.GetString(0));
            }
        }

        return (cycle, played, last);
    }

    public void Insert(IReadOnlyList<ScheduleEntry> entries)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO schedule_entries
                (channel_id, item_type, item_id, show_id, season_number, episode_number,
                 title, subtitle, cycle, start_ts, end_ts)
            VALUES ($channel, $type, $item, $show, $season, $episode, $title, $subtitle, $cycle, $start, $end);
            """;
        foreach (var entry in entries)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$channel", entry.ChannelId);
            command.Parameters.AddWithValue("$type", entry.ItemType);
            command.Parameters.AddWithValue("$item", entry.ItemId);
            command.Parameters.AddWithValue("$show", (object?)entry.ShowId ?? DBNull.Value);
            command.Parameters.AddWithValue("$season", (object?)entry.Season ?? DBNull.Value);
            command.Parameters.AddWithValue("$episode", (object?)entry.Episode ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", entry.Title);
            command.Parameters.AddWithValue("$subtitle", (object?)entry.Subtitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$cycle", entry.Cycle);
            command.Parameters.AddWithValue("$start", entry.StartTs);
            command.Parameters.AddWithValue("$end", entry.EndTs);
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Removes what hasn't started yet. Whatever is on air now keeps playing.</summary>
    public void DeleteFuture(long channelId, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM schedule_entries WHERE channel_id = $id AND start_ts > $now;";
        command.Parameters.AddWithValue("$id", channelId);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Begins a new pass after the air order changes, so the new order runs from its start.
    /// The last row, normally what's on air, joins the new pass so it doesn't air again straight away.
    /// </summary>
    public void StartNewCycle(long channelId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE schedule_entries
            SET cycle = (SELECT MAX(cycle) + 1 FROM schedule_entries WHERE channel_id = $id)
            WHERE id = (SELECT id FROM schedule_entries WHERE channel_id = $id ORDER BY start_ts DESC LIMIT 1);
            """;
        command.Parameters.AddWithValue("$id", channelId);
        command.ExecuteNonQuery();
    }

    /// <summary>True if something scheduled to air is no longer in the index.</summary>
    public bool HasRemovedItems(long channelId, long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM schedule_entries s
                WHERE s.channel_id = $id AND s.start_ts > $now AND (
                    (s.item_type = 'movie' AND NOT EXISTS (SELECT 1 FROM movies m WHERE m.id = s.item_id)) OR
                    (s.item_type = 'episode' AND NOT EXISTS (SELECT 1 FROM episodes e WHERE e.id = s.item_id))));
            """;
        command.Parameters.AddWithValue("$id", channelId);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<ScheduleEntry> Range(long from, long to)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT channel_id, item_type, item_id, show_id, season_number, episode_number,
                   title, subtitle, cycle, start_ts, end_ts
            FROM schedule_entries
            WHERE end_ts > $from AND start_ts < $to
            ORDER BY channel_id, start_ts;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);

        var result = new List<ScheduleEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ScheduleEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8), reader.GetInt64(9), reader.GetInt64(10)));
        }

        return result;
    }

    /// <summary>Drops old rows, keeping what channels need to remember their place.</summary>
    public void PruneHistory(long now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM schedule_entries
            WHERE end_ts < $cutoff
              AND cycle <> (SELECT MAX(cycle) FROM schedule_entries l
                            WHERE l.channel_id = schedule_entries.channel_id);
            """;
        command.Parameters.AddWithValue("$cutoff", now - HistorySeconds);
        command.ExecuteNonQuery();
    }
}
