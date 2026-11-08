using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;
using PrimeTime.Server.Jellyfin;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Library;

/// <summary>Reads the selected Jellyfin libraries and brings the local media index up to date.</summary>
public sealed class LibraryScanTask(
    IServiceScopeFactory scopes,
    JellyfinConnectionStore connections,
    SettingsStore settings,
    LibraryStore libraries,
    MediaIndexStore index,
    ScheduleService schedule,
    CollectionStore collections,
    ILogger<LibraryScanTask> logger) : IScheduledTask
{
    public const string TaskKey = "library-scan";

    private const int PageSize = 500;
    private const string Movie = "Movie";
    private const string Series = "Series";
    private const string Episode = "Episode";

    public string Key => TaskKey;
    public string Name => "Library scan";
    public string Description => "Updates the index of movies and shows from your selected Jellyfin libraries.";
    public int DefaultIntervalMinutes => 4 * 60;

    public string? NotReadyReason()
    {
        if (!connections.Exists())
        {
            return "Connect a Jellyfin server first.";
        }

        return libraries.AnySelected() ? null : "Choose which libraries to include first.";
    }

    public async Task<TaskOutcome> RunAsync(TaskRunContext context, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<JellyfinClient>();
        var service = scope.ServiceProvider.GetRequiredService<LibraryService>();
        var connection = connections.Get() ?? throw new JellyfinException("No Jellyfin server is connected.");

        context.Report("Checking Jellyfin's library list…");
        var liveIds = await service.RefreshAsync(connection, ct);
        if (liveIds.Count > 0)
        {
            libraries.DeleteExcept(liveIds);
        }

        var included = libraries.List().Where(l => l.Selected).ToList();
        var selected = included.Where(l => LibraryTypes.HoldsMedia(l.CollectionType)).ToList();
        var collectionLibrary = included.FirstOrDefault(l => l.CollectionType == LibraryTypes.Collections);

        int added = 0, removed = 0;
        var errors = new List<string>();
        foreach (var library in selected)
        {
            try
            {
                var (libraryAdded, libraryRemoved) =
                    await ScanLibraryAsync(client, connection, library, context, ct);
                added += libraryAdded;
                removed += libraryRemoved;
                libraries.RecordScan(library.Id, DateTimeOffset.UtcNow, error: null);
            }
            catch (JellyfinException ex)
            {
                // Keep what's indexed and carry on; one bad library shouldn't block the others.
                logger.LogWarning("Scan of library {Library} failed: {Message}", library.Name, ex.Message);
                libraries.RecordScan(library.Id, scannedAt: null, ex.Message);
                errors.Add($"{library.Name}: {ex.Message}");
            }
        }

        // Collections only add to what's indexed, so a failure here leaves the movies and shows scan intact.
        HashSet<string> changedCollections = [];
        string? collectionError = null;
        if (collectionLibrary is null)
        {
            changedCollections = ForgetCollections();
        }
        else
        {
            try
            {
                changedCollections = await SyncCollectionsAsync(client, connection, context, ct);
                libraries.RecordScan(collectionLibrary.Id, DateTimeOffset.UtcNow, error: null);
            }
            catch (JellyfinException ex)
            {
                logger.LogWarning("Collection sync failed: {Message}", ex.Message);
                libraries.RecordScan(collectionLibrary.Id, scannedAt: null, ex.Message);
                collectionError = $"Collections: {ex.Message}";
            }
        }

        var totals = libraries.List().Where(l => l.Selected).ToList();
        var summary =
            $"{Count(totals.Sum(l => l.Movies), "movie")}, {Count(totals.Sum(l => l.Shows), "show")}, " +
            $"{Count(totals.Sum(l => l.Episodes), "episode")}" +
            (collectionLibrary is null ? "" : $", {Count(collections.List().Count, "collection")}") +
            $" indexed. {added:N0} added, {removed:N0} removed.";

        var status = errors.Count == selected.Count && selected.Count > 0 ? TaskRunStatus.Failed
            : errors.Count > 0 || collectionError is not null ? TaskRunStatus.Partial
            : TaskRunStatus.Succeeded;
        if (collectionError is not null)
        {
            errors.Add(collectionError);
        }

        if (status != TaskRunStatus.Failed && selected.Count > 0)
        {
            settings.MarkSetupComplete();
        }

        try
        {
            schedule.RefreshAll(changedCollections);
        }
        catch (Exception ex)
        {
            // The scan itself succeeded; the Guide schedule task will try again.
            logger.LogError(ex, "Couldn't update channel schedules after the scan");
        }

        return new TaskOutcome(status, summary, errors.Count == 0 ? null : string.Join(" ", errors));
    }

    /// <summary>Drops collections after the admin leaves them out, and returns the IDs that went.</summary>
    private HashSet<string> ForgetCollections()
    {
        var ids = collections.List().Select(c => c.Id).ToHashSet();
        if (ids.Count > 0)
        {
            collections.DeleteAll();
        }

        return ids;
    }

    /// <summary>Mirrors Jellyfin's collections and returns the IDs of any whose contents changed.</summary>
    private async Task<HashSet<string>> SyncCollectionsAsync(
        JellyfinClient client, SavedJellyfinConnection connection, TaskRunContext context, CancellationToken ct)
    {
        var deviceId = settings.GetOrCreateDeviceId();
        context.Report("Checking collections…");

        var (live, listComplete) = await FetchAllAsync((start, limit) => client.GetCollectionsPageAsync(
            connection.BaseUrl, deviceId, connection.Token, connection.UserId, start, limit, ct), ct);

        var known = collections.Members();
        var existingIds = collections.List().Select(c => c.Id).ToHashSet();
        var changed = new HashSet<string>();

        for (var i = 0; i < live.Count; i++)
        {
            var collection = live[i];
            context.Report("Checking collections…", i + 1, live.Count);

            var (children, complete) = await FetchAllAsync((start, limit) => client.GetCollectionItemsPageAsync(
                connection.BaseUrl, deviceId, connection.Token, connection.UserId, collection.Id!, start, limit, ct), ct);
            if (!complete)
            {
                logger.LogWarning("Didn't get all of collection {Name}; keeping its previous contents", collection.Name);
                continue;
            }

            var itemIds = children.Select(c => c.Id!).ToHashSet();
            var previous = known.GetValueOrDefault(collection.Id!) ?? [];
            if (!existingIds.Contains(collection.Id!) || !previous.SetEquals(itemIds))
            {
                changed.Add(collection.Id!);
            }

            collections.Replace(collection.Id!, collection.Name ?? "(unnamed)", collection.SortName, itemIds);
        }

        if (listComplete)
        {
            var liveIds = live.Select(c => c.Id!).ToHashSet();
            foreach (var id in existingIds.Where(id => !liveIds.Contains(id)))
            {
                collections.Delete(id);
                changed.Add(id);
            }
        }

        return changed;
    }

    /// <summary>Reads every page. The flag is false if the pages fell short of Jellyfin's total.</summary>
    private static async Task<(List<JellyfinMediaItem> Items, bool Complete)> FetchAllAsync(
        Func<int, int, Task<JellyfinMediaPage>> fetchPage, CancellationToken ct)
    {
        var items = new Dictionary<string, JellyfinMediaItem>();
        var start = 0;
        int? total = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await fetchPage(start, PageSize);
            var received = page.Items ?? [];
            total = page.TotalRecordCount ?? total;
            foreach (var item in received.Where(i => i.Id is not null))
            {
                items[item.Id!] = item;
            }

            start += received.Count;
            if (received.Count == 0 || (total is { } known ? start >= known : received.Count < PageSize))
            {
                return (items.Values.ToList(), total is null || items.Count >= total);
            }
        }
    }

    private async Task<(int Added, int Removed)> ScanLibraryAsync(
        JellyfinClient client, SavedJellyfinConnection connection, StoredLibrary library,
        TaskRunContext context, CancellationToken ct)
    {
        var deviceId = settings.GetOrCreateDeviceId();
        var runId = context.RunId;
        var complete = true;

        if (library.CollectionType is LibraryTypes.Movies or LibraryTypes.Mixed)
        {
            complete &= await ForEachPageAsync(Movie, "movies",
                page => index.UpsertMovies(library.Id, runId, page));
        }

        if (library.CollectionType is LibraryTypes.Shows or LibraryTypes.Mixed)
        {
            var showIds = new HashSet<string>();
            complete &= await ForEachPageAsync(Series, "shows", page =>
            {
                index.UpsertShows(library.Id, runId, page);
                showIds.UnionWith(page.Select(s => s.Id!));
            });

            complete &= await ForEachPageAsync(Episode, "episodes", page =>
            {
                // An episode can only be indexed under a show we just saw.
                var known = page.Where(e => e.SeriesId is not null && showIds.Contains(e.SeriesId)).ToList();
                index.UpsertEpisodes(library.Id, runId, known);
            });
        }

        var added = index.CountAdded(library.Id, runId);
        if (!complete)
        {
            logger.LogWarning(
                "Scan of library {Library} didn't get every item, so nothing was removed from the index this time",
                library.Name);
            return (added, 0);
        }

        return (added, index.RemoveUnseen(library.Id, runId));

        // Returns false if the pages didn't add up to the total Jellyfin reported. Jellyfin has
        // no unique sort key, so items can shift between pages while the library is changing.
        async Task<bool> ForEachPageAsync(
            string itemType, string label, Action<IReadOnlyList<JellyfinMediaItem>> handlePage)
        {
            var seen = new HashSet<string>();
            var start = 0;
            int? total = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await client.GetItemsPageAsync(connection.BaseUrl, deviceId, connection.Token,
                    connection.UserId, library.Id, itemType, start, PageSize, ct);

                var received = page.Items ?? [];
                total = page.TotalRecordCount ?? total;
                var items = received.Where(i => i.Id is not null).ToList();
                handlePage(items);
                seen.UnionWith(items.Select(i => i.Id!));

                start += received.Count;
                context.Report($"{library.Name}: {label}", start, total);
                if (received.Count == 0 || (total is { } known ? start >= known : received.Count < PageSize))
                {
                    return total is null || seen.Count >= total;
                }
            }
        }
    }

    private static string Count(int value, string noun) => $"{value:N0} {noun}{(value == 1 ? "" : "s")}";
}
