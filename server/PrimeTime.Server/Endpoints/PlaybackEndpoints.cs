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
        api.MapPost("/channels/{id:long}/play", async (
            long id, HttpContext http, PlaybackService playback, CancellationToken ct) =>
        {
            try
            {
                var viewer = new StreamViewer(
                    http.User.Identity?.Name ?? "",
                    StreamEndpoints.DescribeBrowser(http.Request.Headers.UserAgent.ToString()),
                    http.Connection.RemoteIpAddress?.ToString());
                var (channel, start) = await playback.StartAsync(id, StreamProfile.Compatible, viewer, ct);
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

        api.MapGet("/play/{playId}/{**path}", RelayStreamAsync);

        api.MapDelete("/play/{playId}", async (string playId, PlaybackService playback, CancellationToken ct) =>
        {
            await playback.StopAsync(playId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Passes Jellyfin's playlists and video segments through unchanged, apart from removing the key and dropping
    /// converted versions of the video from the master playlist (see <see cref="PlaybackService.KeepCopiedVideo"/>).
    /// </summary>
    public static async Task<IResult> RelayStreamAsync(
        string playId, string path, HttpContext http, PlaybackSessions sessions,
        JellyfinConnectionStore connections, SettingsStore settings, JellyfinClient client)
    {
        if (!StreamFile().IsMatch(path) || sessions.Use(playId) is not { } session ||
            connections.Get() is not { } connection)
        {
            return StreamGone(playId, sessions);
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
                var playlist = PlaybackService.RemoveApiKey(await upstream.Content.ReadAsStringAsync(ct));
                if (path == "master.m3u8")
                {
                    playlist = PlaybackService.KeepCopiedVideo(playlist);
                }

                return Results.Text(playlist, "application/vnd.apple.mpegurl");
            }

            http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp2t";
            http.Response.ContentLength = upstream.Content.Headers.ContentLength;
            await CopyCountingAsync(upstream, http.Response.Body, session, ct);
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
    }

    /// <summary>410 for a stream stopped from Settings, so the player says so instead of tuning in again.</summary>
    private static IResult StreamGone(string playId, PlaybackSessions sessions) =>
        sessions.WasStoppedByAdmin(playId)
            ? Results.Json(new { error = "stopped_by_admin" }, statusCode: StatusCodes.Status410Gone)
            : Results.NotFound();

    /// <summary>Copies the video to the viewer, adding up how much was sent for Settings -> Streams.</summary>
    private static async Task CopyCountingAsync(
        HttpResponseMessage upstream, Stream destination, PlaybackSession session, CancellationToken ct)
    {
        await using var source = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                session.Sent(read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Passes the original file through, so a capable player needs no transcoding at all.</summary>
    public static async Task<IResult> RelayDirectAsync(
        string playId, HttpContext http, PlaybackSessions sessions,
        JellyfinConnectionStore connections, SettingsStore settings, JellyfinClient client)
    {
        if (sessions.Use(playId) is not { } session || connections.Get() is not { } connection)
        {
            return StreamGone(playId, sessions);
        }

        var ct = http.RequestAborted;
        var item = Uri.EscapeDataString(session.ItemId);
        try
        {
            using var upstream = await client.OpenStreamAsync(
                connection.BaseUrl, settings.GetOrCreateDeviceId(), connection.Token,
                $"/Videos/{item}/stream?static=true&mediaSourceId={item}", ct,
                http.Request.Headers.Range.FirstOrDefault());
            if (!upstream.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            http.Response.StatusCode = (int)upstream.StatusCode;
            http.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            http.Response.ContentLength = upstream.Content.Headers.ContentLength;
            http.Response.Headers.AcceptRanges = "bytes";
            http.Response.Headers.CacheControl = "no-store";
            if (upstream.Content.Headers.ContentRange is { } contentRange)
            {
                http.Response.Headers.ContentRange = contentRange.ToString();
            }

            session.Delivery = StreamDelivery.DirectPlay;
            await CopyCountingAsync(upstream, http.Response.Body, session, ct);
            return Results.Empty;
        }
        catch (JellyfinException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.Empty;
        }
    }

    // Only the files that make up a stream, so this can't be used to reach anything else on Jellyfin:
    // the playlists, the segments (an fMP4 stream starts with "-1.mp4"), and the text subtitles Jellyfin
    // lists in the playlist for players that show them themselves.
    [GeneratedRegex(@"\A(master\.m3u8|main\.m3u8|hls1/[A-Za-z0-9_-]+/-?\d+\.(ts|mp4|m4s)|" +
                    @"[A-Za-z0-9-]+/Subtitles/\d+/(subtitles\.m3u8|[Ss]tream\.vtt))\z")]
    private static partial Regex StreamFile();
}
