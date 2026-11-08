using PrimeTime.Server.Data;

namespace PrimeTime.Server.Tasks;

/// <summary>
/// Checks every 30 seconds whether a task is due. Due times live in the database,
/// so a schedule carries across restarts and a run missed while stopped happens at startup.
/// </summary>
public sealed class TaskSchedulerService(
    TaskRunner runner,
    ScheduledTaskStore store,
    ILogger<TaskSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                StartDueTasks();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Couldn't check for due tasks");
            }
        }
        while (await WaitForNextTick(timer, stoppingToken));
    }

    private void StartDueTasks()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in runner.Tasks)
        {
            var schedule = store.Get(task.Key);
            if (schedule is not { Enabled: true } || task.NotReadyReason() is not null)
            {
                continue;
            }

            if (schedule.NextRunAt is null)
            {
                store.SetNextRun(task.Key, now + TimeSpan.FromMinutes(schedule.IntervalMinutes));
            }
            else if (schedule.NextRunAt <= now)
            {
                // If another task holds the runner, this stays due and is retried on the next check.
                runner.TryStart(task.Key, "scheduled");
            }
        }
    }

    private static async Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
