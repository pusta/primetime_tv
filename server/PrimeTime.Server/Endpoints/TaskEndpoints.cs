using PrimeTime.Server.Data;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Endpoints;

public sealed record TaskScheduleRequest(bool? Enabled, int? IntervalMinutes);

public static class TaskEndpoints
{
    private const int RecentRunsShown = 10;

    public static void MapTaskEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/tasks").RequireAuthorization();

        api.MapGet("/", (TaskRunner runner, ScheduledTaskStore store) => Results.Ok(new
        {
            minIntervalMinutes = TaskRunner.MinIntervalMinutes,
            maxIntervalMinutes = TaskRunner.MaxIntervalMinutes,
            tasks = runner.Tasks.Select(task => Describe(task, runner, store)),
        }));

        api.MapPut("/{key}", (string key, TaskScheduleRequest body, TaskRunner runner, ScheduledTaskStore store) =>
        {
            var task = runner.Find(key);
            if (task is null)
            {
                return Results.NotFound(new { error = "No such task." });
            }

            if (body.Enabled is null || body.IntervalMinutes is not { } minutes ||
                minutes < TaskRunner.MinIntervalMinutes || minutes > TaskRunner.MaxIntervalMinutes)
            {
                return Results.BadRequest(new { error = "Choose an interval between 15 minutes and 30 days." });
            }

            runner.UpdateSchedule(key, body.Enabled.Value, minutes);
            return Results.Ok(Describe(task, runner, store));
        });

        api.MapPost("/{key}/run", (string key, TaskRunner runner) =>
            runner.TryStart(key, "manual") switch
            {
                TaskStartResult.Started => Results.Accepted(),
                TaskStartResult.Busy => Results.Conflict(new { error = "A task is already running." }),
                TaskStartResult.NotReady => Results.BadRequest(new { error = runner.Find(key)!.NotReadyReason() }),
                _ => Results.NotFound(new { error = "No such task." }),
            });

        api.MapPost("/{key}/cancel", (string key, TaskRunner runner) =>
            runner.RequestCancel(key)
                ? Results.Accepted()
                : Results.Conflict(new { error = "That task isn't running." }));
    }

    private static object Describe(IScheduledTask task, TaskRunner runner, ScheduledTaskStore store)
    {
        var schedule = store.Get(task.Key)!;
        var current = runner.Current;
        return new
        {
            key = task.Key,
            name = task.Name,
            description = task.Description,
            enabled = schedule.Enabled,
            intervalMinutes = schedule.IntervalMinutes,
            nextRunAt = schedule.NextRunAt,
            notReadyReason = task.NotReadyReason(),
            running = current?.Key == task.Key ? current : null,
            recentRuns = store.RecentRuns(task.Key, RecentRunsShown),
        };
    }
}
