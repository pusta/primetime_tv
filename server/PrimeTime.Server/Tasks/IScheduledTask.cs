namespace PrimeTime.Server.Tasks;

/// <summary>What a task reports when it ends without throwing.</summary>
public sealed record TaskOutcome(string Status, string? Summary, string? Error = null);

/// <summary>Passed to a running task: its run ID and a way to publish live progress.</summary>
public sealed class TaskRunContext(long runId, Action<string, int?, int?> report)
{
    public long RunId { get; } = runId;

    public void Report(string message, int? processed = null, int? total = null) =>
        report(message, processed, total);
}

/// <summary>A background job that appears under Settings → Scheduled Tasks.</summary>
public interface IScheduledTask
{
    string Key { get; }
    string Name { get; }
    string Description { get; }
    int DefaultIntervalMinutes { get; }

    /// <summary>Why the task can't run right now, or null when it can.</summary>
    string? NotReadyReason();

    Task<TaskOutcome> RunAsync(TaskRunContext context, CancellationToken ct);
}
