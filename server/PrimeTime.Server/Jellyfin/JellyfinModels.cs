namespace PrimeTime.Server.Jellyfin;

// Shapes of the Jellyfin API responses we read. Jellyfin uses PascalCase JSON;
// JellyfinClient deserializes case-insensitively, so these map directly.

public sealed record JellyfinPublicSystemInfo(
    string? ServerName,
    string? Version,
    string? Id,
    string? ProductName,
    bool? StartupWizardCompleted);

public sealed record JellyfinAuthResult(JellyfinAuthUser? User, string? AccessToken, string? ServerId);

public sealed record JellyfinAuthUser(string? Id, string? Name);

public sealed record JellyfinVirtualFolder(
    string? Name,
    string? CollectionType,
    string? ItemId,
    List<string>? Locations);

public sealed record JellyfinItemsResult(List<JellyfinBaseItem>? Items);

public sealed record JellyfinBaseItem(string? Id, string? Name, string? CollectionType);

public sealed record JellyfinMediaPage(List<JellyfinMediaItem>? Items, int? TotalRecordCount);

public sealed record JellyfinNamedItem(string? Name);

/// <summary>One movie, series or episode from GET /Items.</summary>
public sealed record JellyfinMediaItem(
    string? Id,
    string? Name,
    string? SortName,
    int? ProductionYear,
    string? Overview,
    long? RunTimeTicks,
    List<string>? Genres,
    List<JellyfinNamedItem>? Studios,
    List<string>? Tags,
    string? OfficialRating,
    double? CommunityRating,
    string? PremiereDate,
    string? DateCreated,
    Dictionary<string, string?>? ProviderIds,
    string? Status,
    string? SeriesId,
    int? ParentIndexNumber,
    int? IndexNumber);

// What PrimeTime returns to its own UI.

public sealed record ServerSummary(string Url, string? Name, string? Version, string? Id);

public sealed record LibrarySummary(string? Id, string Name, string CollectionType, IReadOnlyList<string> Locations);
