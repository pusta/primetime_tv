using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;
using PrimeTime.Server.Playback;

namespace PrimeTime.Server.Endpoints;

/// <summary>Settings -> Streams: who is watching what right now, and the last few streams.</summary>
public static class StreamEndpoints
{
    private const int RecentShown = 10;

    // A player asks for the next piece of video every few seconds; longer than this and it's likely paused.
    private const int PausedAfterSeconds = 30;

    public static void MapStreamEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/streams").RequireAuthorization();

        api.MapGet("/", async (
            PlaybackService playback, StreamHistoryStore history, ChannelStore channels, ScheduleStore schedule,
            CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var active = await playback.ActiveAsync(ct);
            var channelsById = channels.List().ToDictionary(c => c.Id);
            return Results.Ok(new
            {
                now,
                active = active.Select(session =>
                {
                    channelsById.TryGetValue(session.ChannelId, out var channel);
                    // A channel keeps playing into the next program, so show what's on it now.
                    var program = schedule.OnAir(session.ChannelId, now);
                    var idle = (long)session.Idle.TotalSeconds;
                    return new
                    {
                        id = session.HistoryId,
                        channel = channel is null ? null : DescribeChannel(channel),
                        program = program is null
                            ? null
                            : new
                            {
                                type = program.ItemType,
                                title = program.Title,
                                subtitle = program.Subtitle,
                                start = program.StartTs,
                                end = program.EndTs,
                            },
                        viewer = session.Viewer.Name,
                        client = session.Viewer.Client,
                        address = session.Viewer.Address,
                        delivery = session.Delivery,
                        transcodeReasons = session.TranscodeReasons,
                        video = session.Video,
                        started = session.StartedTs,
                        idleSeconds = idle,
                        state = idle < PausedAfterSeconds ? "playing" : "paused",
                        bytesSent = session.BytesSent,
                    };
                }),
                recent = history.Recent(RecentShown, active.Select(s => s.HistoryId).ToList()).Select(stream => new
                {
                    id = stream.Id,
                    channel = channelsById.TryGetValue(stream.ChannelId, out var channel)
                        ? DescribeChannel(channel)
                        : new { id = stream.ChannelId, number = stream.ChannelNumber, name = stream.ChannelName, color = (string?)null },
                    title = stream.Title,
                    subtitle = stream.Subtitle,
                    viewer = stream.Viewer,
                    client = stream.Client,
                    address = stream.Address,
                    delivery = stream.Delivery,
                    video = stream.Video,
                    started = stream.StartedTs,
                    ended = stream.EndedTs,
                    // No end time means PrimeTime stopped while it was playing.
                    endReason = stream.EndReason ?? "interrupted",
                    bytesSent = stream.BytesSent,
                }),
            });
        });

        api.MapDelete("/{id:long}", async (long id, PlaybackService playback, CancellationToken ct) =>
            await playback.StopByHistoryIdAsync(id, ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "That stream has already stopped." }));
    }

    private static object DescribeChannel(Channel channel) => new
    {
        id = channel.Id,
        number = channel.Number,
        name = channel.Name,
        color = channel.Color,
    };

    /// <summary>A short name for a browser from its user agent, such as "Safari on macOS".</summary>
    public static string DescribeBrowser(string userAgent)
    {
        string? browser =
            userAgent.Contains("Edg/", StringComparison.Ordinal) ? "Edge" :
            userAgent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox" :
            userAgent.Contains("Chrome/", StringComparison.Ordinal) || userAgent.Contains("CriOS/", StringComparison.Ordinal) ? "Chrome" :
            userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari" :
            null;
        string? system =
            userAgent.Contains("iPhone", StringComparison.Ordinal) ? "iPhone" :
            userAgent.Contains("iPad", StringComparison.Ordinal) ? "iPad" :
            userAgent.Contains("Android", StringComparison.Ordinal) ? "Android" :
            userAgent.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS" :
            userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows" :
            userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux" :
            null;
        return (browser, system) switch
        {
            (null, null) => "Web browser",
            (null, _) => $"Web browser on {system}",
            (_, null) => browser,
            _ => $"{browser} on {system}",
        };
    }
}
