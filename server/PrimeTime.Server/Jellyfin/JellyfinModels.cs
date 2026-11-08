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

public sealed record JellyfinPerson(string? Name, string? Role, string? Type);

public sealed record JellyfinItemDetails(
    string? Id,
    string? Name,
    string? Type,
    string? Overview,
    int? ProductionYear,
    string? OfficialRating,
    double? CommunityRating,
    long? RunTimeTicks,
    string? PremiereDate,
    List<string>? Genres,
    List<JellyfinNamedItem>? Studios,
    List<JellyfinPerson>? People,
    List<string>? Taglines,
    string? SeriesId,
    string? SeriesName,
    int? ParentIndexNumber,
    int? IndexNumber);

public sealed record JellyfinItemDetailsPage(List<JellyfinItemDetails>? Items);

public sealed record JellyfinUserPolicy(bool IsAdministrator, bool IsDisabled);

public sealed record JellyfinUser(string? Id, JellyfinUserPolicy? Policy);

public sealed record JellyfinMediaSource(string? TranscodingUrl);

public sealed record JellyfinPlaybackInfo(List<JellyfinMediaSource>? MediaSources, string? PlaySessionId);

// What PrimeTime returns to its own UI.

public sealed record ServerSummary(string Url, string? Name, string? Version, string? Id);

public sealed record LibrarySummary(string? Id, string Name, string CollectionType, IReadOnlyList<string> Locations);
