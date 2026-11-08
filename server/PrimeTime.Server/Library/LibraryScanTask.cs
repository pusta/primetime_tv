using PrimeTime.Server.Data;
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

        var selected = libraries.List()
            .Where(l => l.Selected && LibraryTypes.IsSupported(l.CollectionType))
            .ToList();

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

        var totals = libraries.List().Where(l => l.Selected).ToList();
        var summary =
            $"{Count(totals.Sum(l => l.Movies), "movie")}, {Count(totals.Sum(l => l.Shows), "show")}, " +
            $"{Count(totals.Sum(l => l.Episodes), "episode")} indexed. {added:N0} added, {removed:N0} removed.";

        var status = errors.Count == 0 ? TaskRunStatus.Succeeded
            : errors.Count < selected.Count ? TaskRunStatus.Partial
            : TaskRunStatus.Failed;
        return new TaskOutcome(status, summary, errors.Count == 0 ? null : string.Join(" ", errors));
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
