namespace PrimeTime.Server.Data;

public static class TaskRunStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public sealed record ScheduledTaskRow(string Key, bool Enabled, int IntervalMinutes, DateTimeOffset? NextRunAt);

public sealed record TaskRunRow(
    long Id,
    string Trigger,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Summary,
    string? Error);

/// <summary>Schedules and run history for background tasks.</summary>
public sealed class ScheduledTaskStore(Database database)
{
    private const int RunsKeptPerTask = 50;

    public void EnsureTask(string key, int defaultIntervalMinutes)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO scheduled_tasks (key, enabled, interval_minutes) VALUES ($key, 1, $interval);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$interval", defaultIntervalMinutes);
        command.ExecuteNonQuery();
    }

    public ScheduledTaskRow? Get(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, interval_minutes, next_run_at FROM scheduled_tasks WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ScheduledTaskRow(
            key,
            reader.GetInt64(0) == 1,
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)));
    }

    public void UpdateSchedule(string key, bool enabled, int intervalMinutes, DateTimeOffset? nextRunAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scheduled_tasks
            SET enabled = $enabled, interval_minutes = $interval, next_run_at = $next
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval", intervalMinutes);
        command.Parameters.AddWithValue("$next", (object?)nextRunAt?.ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void SetNextRun(string key, DateTimeOffset? nextRunAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE scheduled_tasks SET next_run_at = $next WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$next", (object?)nextRunAt?.ToString("O") ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public long StartRun(string key, string trigger, DateTimeOffset startedAt)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO task_runs (task_key, trigger, status, started_at)
            VALUES ($key, $trigger, $status, $startedAt)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$trigger", trigger);
        command.Parameters.AddWithValue("$status", TaskRunStatus.Running);
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void FinishRun(string key, long runId, string status, string? summary, string? error, DateTimeOffset finishedAt)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE task_runs
                SET status = $status, summary = $summary, error = $error, finished_at = $finishedAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$id", runId);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
            update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            update.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("O"));
            update.ExecuteNonQuery();
        }

        using (var prune = connection.CreateCommand())
        {
            prune.Transaction = tx;
            prune.CommandText = """
                DELETE FROM task_runs
                WHERE task_key = $key AND id NOT IN
                    (SELECT id FROM task_runs WHERE task_key = $key ORDER BY id DESC LIMIT $keep);
                """;
            prune.Parameters.AddWithValue("$key", key);
            prune.Parameters.AddWithValue("$keep", RunsKeptPerTask);
            prune.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<TaskRunRow> RecentRuns(string key, int limit)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, trigger, status, started_at, finished_at, summary, error
            FROM task_runs WHERE task_key = $key ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<TaskRunRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TaskRunRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return result;
    }

    public DateTimeOffset? LastFinishedAt(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(finished_at) FROM task_runs WHERE task_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string value ? DateTimeOffset.Parse(value) : null;
    }

    /// <summary>
    /// Closes runs left "running" by a crash or restart, and returns the affected task keys
    /// so they can be run again soon.
    /// </summary>
    public IReadOnlyList<string> MarkAbandonedRunsInterrupted(DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE task_runs
            SET status = $interrupted, finished_at = $now, error = 'PrimeTime stopped before this run finished.'
            WHERE status = $running
            RETURNING task_key;
            """;
        command.Parameters.AddWithValue("$interrupted", TaskRunStatus.Interrupted);
        command.Parameters.AddWithValue("$running", TaskRunStatus.Running);
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        var keys = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys.Distinct().ToList();
    }
}
