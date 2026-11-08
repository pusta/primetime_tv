using System.Security.Cryptography;
using System.Text;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

public sealed record ScheduleRefreshResult(int Channels, DateTimeOffset Through);

/// <summary>Keeps a rolling, back-to-back timeline stored for every channel.</summary>
public sealed class ScheduleService(
    ChannelStore channels,
    ScheduleStore schedule,
    CatalogStore catalog,
    ILogger<ScheduleService> logger)
{
    public const int HorizonDays = 7;

    // Generation reads the timeline and then writes to the end of it, so only one may run at once.
    private readonly Lock _gate = new();

    /// <summary>
    /// Replaces a channel's future after its rules change. What's on air finishes first.
    /// A changed air order starts again from the top of the new order.
    /// </summary>
    public void RebuildChannel(long channelId, bool airOrderChanged = false)
    {
        lock (_gate)
        {
            var channel = channels.Get(channelId);
            if (channel is null)
            {
                return;
            }

            var now = Now();
            schedule.DeleteFuture(channelId, now);
            if (airOrderChanged)
            {
                schedule.StartNewCycle(channelId);
            }

            Extend(channel, catalog.Load(), now);
        }
    }

    /// <summary>
    /// Tops up every channel. A channel's future is replaced first if it includes removed items
    /// or the channel draws on a collection whose contents just changed.
    /// </summary>
    public ScheduleRefreshResult RefreshAll(IReadOnlySet<string>? changedCollections = null)
    {
        lock (_gate)
        {
            var now = Now();
            var snapshot = catalog.Load();
            var all = channels.List();
            foreach (var channel in all)
            {
                var collectionChanged = channel.Definition.Mode == ChannelMode.Collection &&
                                        changedCollections is not null &&
                                        channel.Definition.ItemIds.Any(changedCollections.Contains);
                if (collectionChanged || schedule.HasRemovedItems(channel.Id, now))
                {
                    logger.LogInformation("Rebuilding channel {Number}: its content changed", channel.Number);
                    schedule.DeleteFuture(channel.Id, now);
                }

                Extend(channel, snapshot, now);
            }

            schedule.PruneHistory(now);
            return new ScheduleRefreshResult(all.Count, DateTimeOffset.FromUnixTimeSeconds(Horizon(now)));
        }
    }

    public void DeleteAllChannels()
    {
        lock (_gate)
        {
            channels.DeleteAll();
        }
    }

    private void Extend(Channel channel, CatalogSnapshot snapshot, long now)
    {
        var horizon = Horizon(now);

        // A gap only happens if PrimeTime was off past the end of the schedule; resume from now.
        var start = Math.Max(schedule.LastEndTs(channel.Id) ?? 0, now - now % 60);
        if (start >= horizon)
        {
            return;
        }

        var titles = new ChannelMatcher(channel.Definition).Select(snapshot);
        if (titles.Count == 0)
        {
            return;
        }

        var items = channel.Definition.ContentType == ChannelContent.Movies
            ? titles.Select(MovieItem).ToList()
            : titles.SelectMany(show => EpisodeItems(show, snapshot)).ToList();
        schedule.Insert(Plan(channel, items, start, horizon));
    }

    /// <summary>Airs everything once in the channel's air order, then goes round again.</summary>
    private List<ScheduleEntry> Plan(Channel channel, IReadOnlyList<AirItem> items, long start, long horizon)
    {
        var order = channel.Definition.AirOrder;
        var (cycle, played, last) = schedule.CurrentCycle(channel.Id);
        var entries = new List<ScheduleEntry>();
        var queue = new Queue<AirItem>(Arranged(items, order, channel.Id, cycle).Where(i => !played.Contains(i.Id)));

        for (var time = start; time < horizon;)
        {
            if (queue.Count == 0)
            {
                cycle++;
                var next = Arranged(items, order, channel.Id, cycle);
                if (next.Count > 1 && next[0].Id == last)
                {
                    (next[0], next[1]) = (next[1], next[0]);
                }

                queue = new Queue<AirItem>(next);
            }

            var item = queue.Dequeue();
            var end = time + item.RuntimeSeconds;
            entries.Add(new ScheduleEntry(
                channel.Id, item.ItemType, item.Id, item.ShowId, item.Season, item.Episode,
                item.Title, item.Subtitle, cycle, time, end));
            last = item.Id;
            time = end;
        }

        return entries;
    }

    private static AirItem MovieItem(CatalogTitle movie) => new(
        ScheduleItemType.Movie, movie.Id, null, null, null,
        movie.Title, movie.Year?.ToString(), movie.RuntimeSeconds, movie.Released);

    /// <summary>An episode with no air date takes the date of the one before it, so it stays in place.</summary>
    private static IEnumerable<AirItem> EpisodeItems(CatalogTitle show, CatalogSnapshot snapshot)
    {
        var episodes = snapshot.EpisodesByShow[show.Id];
        var aired = episodes.FirstOrDefault(e => e.AirDate is not null)?.AirDate ?? show.Released;
        foreach (var episode in episodes)
        {
            aired = episode.AirDate ?? aired;
            yield return new AirItem(
                ScheduleItemType.Episode, episode.Id, show.Id, episode.Season, episode.Episode,
                show.Title, $"S{episode.Season}E{episode.Episode} · {episode.Title}", episode.RuntimeSeconds, aired);
        }
    }

    /// <summary>Puts items in air order. Anything with no date at all goes last either way.</summary>
    private static List<AirItem> Arranged(IReadOnlyList<AirItem> items, string order, long channelId, int cycle)
    {
        if (order == AirOrder.Random)
        {
            return Shuffled(items, channelId, cycle);
        }

        // OrderBy is stable, so items sharing a date stay in title and episode order.
        var dated = items.Where(i => i.Aired is not null).OrderBy(i => i.Aired).ToList();
        if (order == AirOrder.NewestFirst)
        {
            dated.Reverse();
        }

        dated.AddRange(items.Where(i => i.Aired is null));
        return dated;
    }

    /// <summary>A stable shuffle: the same channel and cycle always give the same order.</summary>
    private static List<AirItem> Shuffled(IReadOnlyList<AirItem> items, long channelId, int cycle) =>
        items.OrderBy(i => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{channelId}:{cycle}:{i.Id}"))),
            StringComparer.Ordinal).ToList();

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long Horizon(long now) => now + HorizonDays * 24 * 3600;

    /// <summary>One movie or episode ready to be placed on the timeline.</summary>
    private sealed record AirItem(
        string ItemType, string Id, string? ShowId, int? Season, int? Episode,
        string Title, string? Subtitle, long RuntimeSeconds, DateTimeOffset? Aired);
}
