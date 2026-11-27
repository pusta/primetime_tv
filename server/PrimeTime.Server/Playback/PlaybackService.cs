using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;
using PrimeTime.Server.Jellyfin;

namespace PrimeTime.Server.Playback;

/// <summary>One viewer's stream. Holds where it lives on Jellyfin, and never the API key.</summary>
public sealed class PlaybackSession(string id, string itemId, string streamPath, string masterQuery, string playSessionId)
{
    private long _lastUsed = Environment.TickCount64;

    public string Id { get; } = id;
    public string ItemId { get; } = itemId;
    public string StreamPath { get; } = streamPath;
    public string MasterQuery { get; } = masterQuery;
    public string PlaySessionId { get; } = playSessionId;
    private long _bytesSent;

    public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUsed));

    /// <summary>Its row in the stream history, which is also the ID Settings uses for it.</summary>
    public long HistoryId { get; init; }
    public long ChannelId { get; init; }
    public long StartedTs { get; init; }
    public StreamViewer Viewer { get; init; } = new("", "", null);
    public string? Video { get; init; }

    /// <summary>How Jellyfin delivers it: see <see cref="StreamDelivery"/>. An app can switch to the file itself.</summary>
    public string Delivery { get; set; } = StreamDelivery.DirectStream;
    public string? TranscodeReasons { get; init; }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void Touch() => Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);

    public void Sent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
}

/// <summary>Who is watching: a signed-in user's name or an app's device name, what they use, and from where.</summary>
public sealed record StreamViewer(string Name, string Client, string? Address);

public static class StreamDelivery
{
    /// <summary>Jellyfin converts the video, the audio or both.</summary>
    public const string Transcode = "transcode";

    /// <summary>Jellyfin repackages the original video and audio into HLS without converting them.</summary>
    public const string DirectStream = "direct-stream";

    /// <summary>The player reads the original file.</summary>
    public const string DirectPlay = "direct-play";
}

public sealed class PlaybackSessions
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new();

    // Streams stopped from Settings, so their players can be told why rather than quietly tuning in again.
    private readonly ConcurrentDictionary<string, long> _stoppedByAdmin = new();
    private static readonly TimeSpan StoppedRemembered = TimeSpan.FromHours(1);

    public void MarkStoppedByAdmin(string id)
    {
        var now = Environment.TickCount64;
        foreach (var (old, at) in _stoppedByAdmin)
        {
            if (now - at > StoppedRemembered.TotalMilliseconds)
            {
                _stoppedByAdmin.TryRemove(old, out _);
            }
        }

        _stoppedByAdmin[id] = now;
    }

    public bool WasStoppedByAdmin(string id) => _stoppedByAdmin.ContainsKey(id);

    public void Add(PlaybackSession session) => _sessions[session.Id] = session;

    public PlaybackSession? Use(string id)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        session.Touch();
        return session;
    }

    public PlaybackSession? Remove(string id) => _sessions.TryRemove(id, out var session) ? session : null;

    public IReadOnlyList<PlaybackSession> All() => _sessions.Values.ToList();

    public IReadOnlyList<PlaybackSession> RemoveIdle(TimeSpan olderThan)
    {
        var removed = new List<PlaybackSession>();
        foreach (var session in _sessions.Values.Where(s => s.Idle > olderThan))
        {
            if (_sessions.TryRemove(session.Id, out _))
            {
                removed.Add(session);
            }
        }

        return removed;
    }
}

public sealed record ProgramCredit(string Name, string? Role);

/// <summary>What the Info panel shows. Show and ShowOverview are only set for episodes.</summary>
public sealed record ProgramInfo(
    string Title,
    string? Show,
    int? Season,
    int? Episode,
    int? Year,
    string? AirDate,
    string? OfficialRating,
    double? CommunityRating,
    int? RuntimeMinutes,
    string? Tagline,
    string? Overview,
    string? ShowOverview,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> Directors,
    IReadOnlyList<ProgramCredit> Cast);

/// <param name="Player">The player the stream was made for, when an app asked; null for the website.</param>
/// <param name="Source">The program's file as Jellyfin describes it, with its video and audio streams.</param>
/// <param name="AudioStreamIndex">The audio stream in the HLS stream, which carries only one.</param>
public sealed record PlayStart(
    PlaybackSession Session,
    Channel Channel,
    ScheduleEntry Program,
    long Now,
    AppPlayer? Player,
    JellyfinMediaSource Source,
    int? AudioStreamIndex);

/// <summary>Asks Jellyfin to stream what's on air. Jellyfin does any transcoding; PrimeTime only passes it on.</summary>
public sealed partial class PlaybackService(
    JellyfinClient client,
    JellyfinConnectionStore connections,
    SettingsStore settings,
    ChannelStore channels,
    ScheduleStore schedule,
    PlaybackSessions sessions,
    StreamHistoryStore history,
    ILogger<PlaybackService> logger)
{
    // A paused player stops asking for video, so this is how long a pause can last.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private const int CastShown = 8;

    /// <summary>Starts a stream for the website. See the other overload.</summary>
    public Task<(Channel? Channel, PlayStart? Start)> StartAsync(
        long channelId, StreamProfile profile, StreamViewer viewer, CancellationToken ct) =>
        StartAsync(channelId, profile, null, null, viewer, ct);

    /// <summary>
    /// Starts a stream for an app. With Auto, HDR and Dolby Vision programs in HEVC go to Apple's player,
    /// which shows them as intended, and everything else to mpv. <paramref name="audioStreamIndex"/> picks the
    /// audio for an HLS stream, which carries only one.
    /// </summary>
    public Task<(Channel? Channel, PlayStart? Start)> StartAsync(
        long channelId, AppPlayer player, int? audioStreamIndex, StreamViewer viewer, CancellationToken ct) =>
        StartAsync(channelId, player == AppPlayer.Native ? StreamProfile.Native : StreamProfile.Mpv,
            player, audioStreamIndex, viewer, ct);

    /// <summary>Returns null if the channel doesn't exist, or a null program if nothing is on air.</summary>
    private async Task<(Channel? Channel, PlayStart? Start)> StartAsync(
        long channelId, StreamProfile profile, AppPlayer? player, int? audioStreamIndex, StreamViewer viewer,
        CancellationToken ct)
    {
        await StopIdleAsync(ct);

        var channel = channels.Get(channelId);
        if (channel is null)
        {
            return (null, null);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var program = schedule.OnAir(channelId, now);
        if (program is null)
        {
            return (channel, null);
        }

        var connection = connections.Get() ?? throw new JellyfinException("No Jellyfin server is connected.");
        var deviceId = settings.GetOrCreateDeviceId();
        var userId = connection.UserId
                     ?? await client.GetAdminUserIdAsync(connection.BaseUrl, deviceId, connection.Token, ct)
                     ?? throw new JellyfinException("Jellyfin has no administrator account to play this as.");

        var info = await client.GetPlaybackInfoAsync(
            connection.BaseUrl, deviceId, connection.Token, program.ItemId, userId, profile, ct, audioStreamIndex);
        if (player == AppPlayer.Auto)
        {
            // Asking costs nothing until the stream is fetched, so ask again for Apple's player if it's HDR.
            player = AppPlayer.Mpv;
            if (info.MediaSources?.FirstOrDefault() is { IsAppleHdr: true })
            {
                profile = StreamProfile.Native;
                player = AppPlayer.Native;
                info = await client.GetPlaybackInfoAsync(
                    connection.BaseUrl, deviceId, connection.Token, program.ItemId, userId, profile, ct,
                    audioStreamIndex);
            }
        }

        var source = info.MediaSources?.FirstOrDefault();
        var url = source?.TranscodingUrl;
        var separator = url?.IndexOf('?') ?? -1;
        var path = separator > 0 ? url![..separator] : "";
        if (source is null || !path.StartsWith('/') || !path.EndsWith("/master.m3u8", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(info.PlaySessionId))
        {
            throw new JellyfinException("Jellyfin couldn't offer a stream for this program.");
        }

        var query = RemoveApiKey(url![separator..]);
        if (profile.SubtitlesInManifest && !query.Contains("EnableSubtitlesInManifest=", StringComparison.OrdinalIgnoreCase))
        {
            query += "&EnableSubtitlesInManifest=true";
        }

        var reasons = TranscodeReasonsIn(query);
        var delivery = reasons is null ? StreamDelivery.DirectStream : StreamDelivery.Transcode;
        var video = DescribeVideo(source.Video);
        var viewerName = viewer.Name.Length > 0 ? viewer.Name : "Unknown";
        var historyId = history.Start(new StreamRecord(
            0, now, null, null, channel.Id, channel.Number, channel.Name, program.Title, program.Subtitle,
            viewerName, viewer.Client, viewer.Address, delivery, video, 0));

        var session = new PlaybackSession(
            RandomNumberGenerator.GetHexString(32, lowercase: true),
            program.ItemId,
            path[..path.LastIndexOf('/')],
            query,
            info.PlaySessionId)
        {
            HistoryId = historyId,
            ChannelId = channel.Id,
            StartedTs = now,
            Viewer = viewer with { Name = viewerName },
            Video = video,
            Delivery = delivery,
            TranscodeReasons = reasons,
        };
        sessions.Add(session);
        return (channel, new PlayStart(
            session, channel, program, now, player, source,
            audioStreamIndex ?? AudioIndexIn(query) ?? source.DefaultAudioStreamIndex));
    }

    /// <summary>The audio stream Jellyfin chose, from the stream URL it made.</summary>
    private static int? AudioIndexIn(string query) =>
        AudioStreamIndexParameter().Match(query) is { Success: true } match &&
        int.TryParse(match.Groups[1].Value, out var index)
            ? index
            : null;

    [GeneratedRegex("""(?i)[?&]AudioStreamIndex=(\d+)""")]
    private static partial Regex AudioStreamIndexParameter();

    /// <summary>
    /// Why Jellyfin is converting the stream, from the stream URL it made, such as "VideoCodecNotSupported".
    /// Null when it only repackages the video and audio, which costs it almost nothing.
    /// </summary>
    private static string? TranscodeReasonsIn(string query)
    {
        if (TranscodeReasonsParameter().Match(query) is not { Success: true } match)
        {
            return null;
        }

        var reasons = Uri.UnescapeDataString(match.Groups[1].Value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(r => r.StartsWith("Video", StringComparison.Ordinal) || r.StartsWith("Audio", StringComparison.Ordinal))
            .ToList();
        return reasons.Count > 0 ? string.Join(", ", reasons) : null;
    }

    [GeneratedRegex("""(?i)[?&]TranscodeReasons=([^&]*)""")]
    private static partial Regex TranscodeReasonsParameter();

    /// <summary>For example "HEVC 4K HDR10" or "H264 1080p".</summary>
    private static string? DescribeVideo(JellyfinMediaStream? video)
    {
        if (video is null)
        {
            return null;
        }

        var size = video.Height switch
        {
            >= 2000 => "4K",
            >= 1400 => "1440p",
            >= 1000 => "1080p",
            >= 700 => "720p",
            > 0 => "SD",
            _ => null,
        };
        var range = video.VideoRangeType is { } type && type != "SDR" && type != "Unknown" ? type : null;
        return string.Join(' ', new[] { video.Codec?.ToUpperInvariant(), size, range }.OfType<string>());
    }

    /// <summary>Asks Jellyfin about the program a stream is playing. Null if the stream or item has gone.</summary>
    public async Task<ProgramInfo?> GetInfoAsync(string playId, CancellationToken ct)
    {
        if (sessions.Use(playId) is not { } session || connections.Get() is not { } connection)
        {
            return null;
        }

        var deviceId = settings.GetOrCreateDeviceId();
        var item = await client.GetItemDetailsAsync(
            connection.BaseUrl, deviceId, connection.Token, connection.UserId, session.ItemId, ct);
        if (item is null)
        {
            return null;
        }

        // An episode lists its guest stars and crew; the regular cast and genres are on the show.
        var show = item.SeriesId is null
            ? null
            : await client.GetItemDetailsAsync(
                connection.BaseUrl, deviceId, connection.Token, connection.UserId, item.SeriesId, ct);

        var actors = Credits(item, "Actor");
        if (actors.Count == 0 && show is not null)
        {
            actors = Credits(show, "Actor");
        }

        return new ProgramInfo(
            item.Name ?? "",
            item.SeriesName ?? show?.Name,
            item.ParentIndexNumber,
            item.IndexNumber,
            item.ProductionYear,
            item.PremiereDate,
            item.OfficialRating ?? show?.OfficialRating,
            item.CommunityRating,
            item.RunTimeTicks is { } ticks ? (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes) : null,
            item.Taglines?.FirstOrDefault() ?? show?.Taglines?.FirstOrDefault(),
            item.Overview,
            show?.Overview,
            item.Genres is { Count: > 0 } ? item.Genres : show?.Genres ?? [],
            (item.Studios is { Count: > 0 } ? item.Studios : show?.Studios ?? [])
                .Select(s => s.Name).OfType<string>().ToList(),
            (item.People ?? []).Where(p => p.Type == "Director").Select(p => p.Name).OfType<string>().ToList(),
            actors.Take(CastShown).ToList());
    }

    private static List<ProgramCredit> Credits(JellyfinItemDetails item, string type) =>
        (item.People ?? [])
            .Where(p => p.Type == type && p.Name is not null)
            .Select(p => new ProgramCredit(p.Name!, string.IsNullOrWhiteSpace(p.Role) ? null : p.Role))
            .ToList();

    public async Task StopAsync(string playId, CancellationToken ct)
    {
        if (sessions.Remove(playId) is { } session)
        {
            await EndAsync(session, StreamEndReason.Stopped, ct);
        }
    }

    /// <summary>What's playing now, after letting go of streams nobody has asked for in a while.</summary>
    public async Task<IReadOnlyList<PlaybackSession>> ActiveAsync(CancellationToken ct)
    {
        await StopIdleAsync(ct);
        return sessions.All().OrderBy(s => s.Idle).ThenBy(s => s.StartedTs).ToList();
    }

    /// <summary>Stops a stream from Settings. The viewer's player stops getting video and shows an error.</summary>
    public async Task<bool> StopByHistoryIdAsync(long historyId, CancellationToken ct)
    {
        if (sessions.All().FirstOrDefault(s => s.HistoryId == historyId) is not { } found ||
            sessions.Remove(found.Id) is not { } session)
        {
            return false;
        }

        sessions.MarkStoppedByAdmin(session.Id);
        await EndAsync(session, StreamEndReason.StoppedByAdmin, ct);
        return true;
    }

    /// <summary>
    /// Drops the versions of an HDR video that Jellyfin converts, for players that can't show HDR, from a master
    /// playlist that also offers the video copied untouched. Converting 4K as it plays is too slow for most servers,
    /// and Apple's player starts on a converted version while the TV is still in SDR, then waits on segments that
    /// don't come in time. A playlist with no copied version, or nothing but copied ones, is left alone.
    /// </summary>
    public static string KeepCopiedVideo(string playlist)
    {
        var lines = playlist.Split('\n');
        var variants = new List<(int Info, int Uri, bool Converted)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            // The variant's URI is the next line that isn't blank or a tag.
            var uri = i + 1;
            while (uri < lines.Length && (lines[uri].Trim().Length == 0 || lines[uri].StartsWith('#')))
            {
                uri++;
            }

            if (uri < lines.Length)
            {
                variants.Add((i, uri, ConvertedVideo().IsMatch(lines[uri])));
                i = uri;
            }
        }

        if (!variants.Any(v => v.Converted) || variants.All(v => v.Converted))
        {
            return playlist;
        }

        var dropped = variants.Where(v => v.Converted).SelectMany(v => new[] { v.Info, v.Uri }).ToHashSet();
        return string.Join('\n', lines.Where((_, index) => !dropped.Contains(index)));
    }

    [GeneratedRegex("""(?i)[?&]AllowVideoStreamCopy=false(&|$|\s)""")]
    private static partial Regex ConvertedVideo();

    /// <summary>Takes the key out of Jellyfin's URLs. The relay authenticates with a header instead.</summary>
    public static string RemoveApiKey(string text) =>
        ApiKeyParameter().Replace(text, m => m.Groups[2].Length > 0 ? m.Groups[1].Value : "");

    [GeneratedRegex("""(?i)([?&])api_?key=[^&\s"]*(&?)""")]
    private static partial Regex ApiKeyParameter();

    private async Task StopIdleAsync(CancellationToken ct)
    {
        foreach (var session in sessions.RemoveIdle(IdleLimit))
        {
            await EndAsync(session, StreamEndReason.Idle, ct);
        }
    }

    private async Task EndAsync(PlaybackSession session, string reason, CancellationToken ct)
    {
        // An idle stream ended when it was last used, not when we noticed.
        var ended = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (reason == StreamEndReason.Idle)
        {
            ended -= (long)session.Idle.TotalSeconds;
        }

        history.End(session.HistoryId, Math.Max(session.StartedTs, ended), reason, session.Delivery, session.BytesSent);
        await StopEncodingAsync(session, ct);
    }

    private async Task StopEncodingAsync(PlaybackSession session, CancellationToken ct)
    {
        if (connections.Get() is not { } connection)
        {
            return;
        }

        try
        {
            await client.StopEncodingAsync(
                connection.BaseUrl, settings.GetOrCreateDeviceId(), connection.Token, session.PlaySessionId, ct);
        }
        catch (JellyfinException ex)
        {
            // Jellyfin also stops an encode by itself once nothing is asking for it.
            logger.LogDebug("Couldn't stop an encode on Jellyfin: {Message}", ex.Message);
        }
    }
}
