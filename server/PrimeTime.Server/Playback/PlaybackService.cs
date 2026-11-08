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
    public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastUsed));

    public void Touch() => Interlocked.Exchange(ref _lastUsed, Environment.TickCount64);
}

public sealed class PlaybackSessions
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new();

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

public sealed record PlayStart(PlaybackSession Session, Channel Channel, ScheduleEntry Program, long Now);

/// <summary>Asks Jellyfin to stream what's on air. Jellyfin does any transcoding; PrimeTime only passes it on.</summary>
public sealed partial class PlaybackService(
    JellyfinClient client,
    JellyfinConnectionStore connections,
    SettingsStore settings,
    ChannelStore channels,
    ScheduleStore schedule,
    PlaybackSessions sessions,
    ILogger<PlaybackService> logger)
{
    // A paused player stops asking for video, so this is how long a pause can last.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private const int CastShown = 8;

    /// <summary>Returns null if the channel doesn't exist, or a null program if nothing is on air.</summary>
    public async Task<(Channel? Channel, PlayStart? Start)> StartAsync(
        long channelId, StreamProfile profile, CancellationToken ct)
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
            connection.BaseUrl, deviceId, connection.Token, program.ItemId, userId, profile, ct);
        var url = info.MediaSources?.FirstOrDefault()?.TranscodingUrl;
        var separator = url?.IndexOf('?') ?? -1;
        var path = separator > 0 ? url![..separator] : "";
        if (!path.StartsWith('/') || !path.EndsWith("/master.m3u8", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(info.PlaySessionId))
        {
            throw new JellyfinException("Jellyfin couldn't offer a stream for this program.");
        }

        var session = new PlaybackSession(
            RandomNumberGenerator.GetHexString(32, lowercase: true),
            program.ItemId,
            path[..path.LastIndexOf('/')],
            RemoveApiKey(url![separator..]),
            info.PlaySessionId);
        sessions.Add(session);
        return (channel, new PlayStart(session, channel, program, now));
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
            await StopEncodingAsync(session, ct);
        }
    }

    /// <summary>Takes the key out of Jellyfin's URLs. The relay authenticates with a header instead.</summary>
    public static string RemoveApiKey(string text) =>
        ApiKeyParameter().Replace(text, m => m.Groups[2].Length > 0 ? m.Groups[1].Value : "");

    [GeneratedRegex("""(?i)([?&])api_?key=[^&\s"]*(&?)""")]
    private static partial Regex ApiKeyParameter();

    private async Task StopIdleAsync(CancellationToken ct)
    {
        foreach (var session in sessions.RemoveIdle(IdleLimit))
        {
            await StopEncodingAsync(session, ct);
        }
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
