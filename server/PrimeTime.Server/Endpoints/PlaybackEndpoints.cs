using System.Text.RegularExpressions;
using PrimeTime.Server.Data;
using PrimeTime.Server.Jellyfin;
using PrimeTime.Server.Playback;

namespace PrimeTime.Server.Endpoints;

public static partial class PlaybackEndpoints
{
    public static void MapPlaybackEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // Tunes in: what's on air now, how far into it we are, and where to get the stream.
        api.MapPost("/channels/{id:long}/play", async (long id, PlaybackService playback, CancellationToken ct) =>
        {
            try
            {
                var (channel, start) = await playback.StartAsync(id, ct);
                if (channel is null)
                {
                    return Results.NotFound(new { error = "That channel doesn't exist." });
                }

                if (start is null)
                {
                    return Results.Conflict(new { error = "Nothing is on this channel right now." });
                }

                return Results.Ok(new
                {
                    playId = start.Session.Id,
                    url = $"/api/play/{start.Session.Id}/master.m3u8",
                    now = start.Now,
                    offsetSeconds = start.Now - start.Program.StartTs,
                    channel = new { channel.Id, channel.Number, channel.Name, channel.LogoUrl },
                    program = new
                    {
                        title = start.Program.Title,
                        subtitle = start.Program.Subtitle,
                        start = start.Program.StartTs,
                        end = start.Program.EndTs,
                    },
                });
            }
            catch (JellyfinException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Details of the program this stream is playing.
        api.MapGet("/play/{playId}/info", async (string playId, PlaybackService playback, CancellationToken ct) =>
        {
            try
            {
                return await playback.GetInfoAsync(playId, ct) is { } info
                    ? Results.Ok(info)
                    : Results.NotFound(new { error = "No details are available for this program." });
            }
            catch (JellyfinException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Passes Jellyfin's playlists and video segments through unchanged, apart from removing the key.
        api.MapGet("/play/{playId}/{**path}", async (
            string playId, string path, HttpContext http, PlaybackSessions sessions,
            JellyfinConnectionStore connections, SettingsStore settings, JellyfinClient client) =>
        {
            if (!StreamFile().IsMatch(path) || sessions.Use(playId) is not { } session ||
                connections.Get() is not { } connection)
            {
                return Results.NotFound();
            }

            var ct = http.RequestAborted;
            var query = http.Request.QueryString.Value ?? "";
            if (path == "master.m3u8" && query.Length == 0)
            {
                query = session.MasterQuery;
            }

            http.Response.Headers.CacheControl = "no-store";
            try
            {
                using var upstream = await client.OpenStreamAsync(
                    connection.BaseUrl, settings.GetOrCreateDeviceId(), connection.Token,
                    $"{session.StreamPath}/{path}{PlaybackService.RemoveApiKey(query)}", ct);
                if (!upstream.IsSuccessStatusCode)
                {
                    return Results.StatusCode(StatusCodes.Status502BadGateway);
                }

                if (path.EndsWith(".m3u8", StringComparison.Ordinal))
                {
                    var playlist = await upstream.Content.ReadAsStringAsync(ct);
                    return Results.Text(PlaybackService.RemoveApiKey(playlist), "application/vnd.apple.mpegurl");
                }

                http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp2t";
                http.Response.ContentLength = upstream.Content.Headers.ContentLength;
                await upstream.Content.CopyToAsync(http.Response.Body, ct);
                return Results.Empty;
            }
            catch (JellyfinException)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty; // The player moved on, which happens all the time when seeking.
            }
        });

        api.MapDelete("/play/{playId}", async (string playId, PlaybackService playback, CancellationToken ct) =>
        {
            await playback.StopAsync(playId, ct);
            return Results.NoContent();
        });
    }

    // Only the files that make up a stream, so this can't be used to reach anything else on Jellyfin.
    [GeneratedRegex(@"\A(master\.m3u8|main\.m3u8|hls1/[A-Za-z0-9_-]+/\d+\.(ts|mp4|m4s))\z")]
    private static partial Regex StreamFile();
}
