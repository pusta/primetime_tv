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
    int? IndexNumber,
    List<JellyfinPerson>? People = null);

/// <summary>What a player can decode. Jellyfin only transcodes what falls outside this.</summary>
/// <param name="Container">The HLS segment container: "ts", or "mp4" for fragmented MP4.</param>
/// <param name="HevcRangeTypes">
/// The HDR formats the player shows in HEVC, separated by "|", or null to leave it to Jellyfin.
/// </param>
/// <param name="SubtitlesInManifest">Lists text subtitles in the HLS playlist, for the player to show itself.</param>
public sealed record StreamProfile(
    string VideoCodecs,
    string AudioCodecs,
    string MaxAudioChannels,
    int MaxBitrate,
    string Container = "ts",
    string? HevcRangeTypes = null,
    bool SubtitlesInManifest = false)
{
    /// <summary>Browsers.</summary>
    public static readonly StreamProfile Compatible = new("h264", "aac", "2", 20_000_000);

    /// <summary>mpv decodes nearly anything, so most files can be passed along without transcoding.</summary>
    public static readonly StreamProfile Mpv = new("h264,hevc", "aac,ac3,eac3,mp3", "8", 120_000_000);

    /// <summary>
    /// Apple's own player (AVPlayer). HEVC is copied into fragmented MP4, which is how Apple plays HDR10,
    /// HLG and Dolby Vision, so the picture reaches the screen untouched and an Apple TV switches modes.
    /// Dolby Vision profile 7 (with an enhancement layer) is left out, so Jellyfin falls back to its HDR10 base.
    /// </summary>
    public static readonly StreamProfile Native = new(
        "h264,hevc",
        "eac3,ac3,aac,alac,mp3",
        "8",
        120_000_000,
        Container: "mp4",
        HevcRangeTypes: "SDR|HDR10|HDR10Plus|HLG|DOVI|DOVIWithHDR10|DOVIWithHLG|DOVIWithSDR|DOVIWithHDR10Plus",
        SubtitlesInManifest: true);
}

/// <summary>The player an app asked for. Auto picks Native for HDR and Dolby Vision, and mpv for the rest.</summary>
public enum AppPlayer
{
    Mpv,
    Native,
    Auto,
}

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

public sealed record JellyfinMediaStream(
    string? Type,
    int Index,
    string? Codec,
    string? Language,
    string? Title,
    string? DisplayTitle,
    int? Channels,
    bool IsDefault,
    bool IsForced,
    string? VideoRange,
    string? VideoRangeType,
    int? Width,
    int? Height);

public sealed record JellyfinMediaSource(
    string? TranscodingUrl,
    List<JellyfinMediaStream>? MediaStreams = null,
    int? DefaultAudioStreamIndex = null)
{
    public JellyfinMediaStream? Video => MediaStreams?.FirstOrDefault(s => s.Type == "Video");

    public IEnumerable<JellyfinMediaStream> AudioStreams =>
        MediaStreams?.Where(s => s.Type == "Audio") ?? [];

    /// <summary>HDR10, HDR10+, HLG or Dolby Vision, in any codec.</summary>
    public bool IsHdr => Video is { } video &&
                         (video.VideoRange == "HDR" ||
                          video.VideoRangeType is { } type && type != "SDR" && type != "Unknown");

    /// <summary>
    /// HDR in HEVC, which Apple's player shows as intended without transcoding. HDR in AV1 or VP9 would
    /// have to be converted for it, so mpv plays those from the original file instead.
    /// </summary>
    public bool IsAppleHdr => IsHdr && string.Equals(Video?.Codec, "hevc", StringComparison.OrdinalIgnoreCase);
}

public sealed record JellyfinPlaybackInfo(List<JellyfinMediaSource>? MediaSources, string? PlaySessionId);

// What PrimeTime returns to its own UI.

public sealed record ServerSummary(string Url, string? Name, string? Version, string? Id);

public sealed record LibrarySummary(string? Id, string Name, string CollectionType, IReadOnlyList<string> Locations);
