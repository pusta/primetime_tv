using PrimeTime.Server.Data;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Guide;

/// <summary>Keeps every channel's schedule filled a week ahead.</summary>
public sealed class GuideScheduleTask(ChannelStore channels, ScheduleService schedule) : IScheduledTask
{
    public string Key => "guide-schedule";
    public string Name => "Guide schedule";
    public string Description => $"Keeps every channel scheduled {ScheduleService.HorizonDays} days ahead.";
    public int DefaultIntervalMinutes => 60;

    public string? NotReadyReason() => channels.Any() ? null : "Add a channel first.";

    public Task<TaskOutcome> RunAsync(TaskRunContext context, CancellationToken ct)
    {
        context.Report("Updating channel schedules…");
        var result = schedule.RefreshAll();
        var label = result.Channels == 1 ? "channel" : "channels";
        return Task.FromResult(new TaskOutcome(
            TaskRunStatus.Succeeded, $"{result.Channels} {label} scheduled {ScheduleService.HorizonDays} days ahead."));
    }
}
