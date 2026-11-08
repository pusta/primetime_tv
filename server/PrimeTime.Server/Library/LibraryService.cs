using PrimeTime.Server.Data;
using PrimeTime.Server.Jellyfin;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Library;

/// <summary>The admin's choice of libraries could not be applied.</summary>
public sealed class LibrarySelectionException(string message) : Exception(message);

public sealed record LibraryOverview(
    bool Configured,
    bool Reachable,
    string? Error,
    bool SelectionConfirmed,
    IReadOnlyList<LibraryView> Libraries);

public sealed record LibraryView(
    string Id,
    string Name,
    string CollectionType,
    IReadOnlyList<string> Locations,
    bool Supported,
    bool Selected,
    bool Available,
    int Movies,
    int Shows,
    int Episodes,
    int Collections,
    DateTimeOffset? LastScannedAt,
    string? LastScanError);

/// <summary>Keeps the stored library list in step with Jellyfin and applies the admin's selection.</summary>
public sealed class LibraryService(
    JellyfinClient client,
    JellyfinConnectionStore connections,
    SettingsStore settings,
    LibraryStore libraries,
    CollectionStore collections,
    TaskRunner runner)
{
    /// <summary>Stored libraries, refreshed from Jellyfin when it can be reached.</summary>
    public async Task<LibraryOverview> GetOverviewAsync(CancellationToken ct)
    {
        var connection = connections.Get();
        if (connection is null)
        {
            return new LibraryOverview(false, false, null, false, []);
        }

        HashSet<string>? liveIds = null;
        string? error = null;
        try
        {
            liveIds = await RefreshAsync(connection, ct);
        }
        catch (JellyfinException ex)
        {
            error = ex.Message;
        }

        var collectionCount = collections.List().Count;
        var views = libraries.List()
            .Select(l => new LibraryView(
                l.Id, l.Name, l.CollectionType, l.Locations,
                LibraryTypes.IsSupported(l.CollectionType),
                l.Selected,
                Available: liveIds?.Contains(l.Id) ?? true,
                l.Movies, l.Shows, l.Episodes,
                Collections: l.CollectionType == LibraryTypes.Collections ? collectionCount : 0,
                l.LastScannedAt, l.LastScanError))
            .ToList();

        return new LibraryOverview(true, liveIds is not null, error, views.Any(l => l.Selected), views);
    }

    /// <summary>
    /// Copies Jellyfin's library list into the store and returns the IDs it found. Nothing is
    /// deleted here; only a scan forgets libraries, so a page load can never empty the index.
    /// </summary>
    public async Task<HashSet<string>> RefreshAsync(SavedJellyfinConnection connection, CancellationToken ct)
    {
        var live = await client.GetLibrariesAsync(
            connection.BaseUrl, settings.GetOrCreateDeviceId(), connection.Token, connection.UserId, ct);
        libraries.Upsert(live);
        return live.Where(l => l.Id is not null).Select(l => l.Id!).ToHashSet();
    }

    /// <summary>Saves which libraries to index and drops indexed items from the ones left out.</summary>
    public async Task ApplySelectionAsync(IReadOnlyCollection<string> libraryIds, CancellationToken ct)
    {
        var connection = connections.Get()
                         ?? throw new LibrarySelectionException("Connect a Jellyfin server first.");

        var requested = libraryIds.ToHashSet();
        if (requested.Count == 0)
        {
            throw new LibrarySelectionException("Select at least one library.");
        }

        var liveIds = await RefreshAsync(connection, ct);
        var supported = libraries.List()
            .Where(l => LibraryTypes.IsSupported(l.CollectionType) && liveIds.Contains(l.Id))
            .ToDictionary(l => l.Id);
        if (!requested.All(supported.ContainsKey))
        {
            throw new LibrarySelectionException("One of those libraries can't be indexed. Reload and try again.");
        }

        // A collection only points at items, so what it holds has to come from a media library.
        if (!requested.Any(id => LibraryTypes.HoldsMedia(supported[id].CollectionType)))
        {
            throw new LibrarySelectionException(
                "Collections play movies and shows from your other libraries, so select at least one of those too.");
        }

        await runner.RunExclusiveAsync(() => libraries.ApplySelection(requested));
    }
}
