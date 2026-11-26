using System.Text.RegularExpressions;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

public static class ChannelContent
{
    public const string Movies = "movies";
    public const string Shows = "shows";
}

public static class ChannelMode
{
    /// <summary>Everything matching the year, genre and studio rules.</summary>
    public const string Filter = "filter";

    /// <summary>Only the titles the admin chose.</summary>
    public const string Pick = "pick";

    /// <summary>Whatever is in the chosen Jellyfin collections. ItemIds holds the collection IDs.</summary>
    public const string Collection = "collection";

    /// <summary>The titles in ItemIds plus the ones most like them, up to SimilarCount more.</summary>
    public const string Similar = "similar";
}

public static class AirOrder
{
    public const string OldestFirst = "oldest";
    public const string NewestFirst = "newest";
    public const string Random = "random";

    public static bool IsValid(string? order) => order is OldestFirst or NewestFirst or Random;

    /// <summary>Movies suit a shuffle; episodes make more sense in the order they aired.</summary>
    public static string DefaultFor(string contentType) =>
        contentType == ChannelContent.Shows ? OldestFirst : Random;
}

/// <summary>What a channel plays. A rule rather than a list, so new content can join by itself.</summary>
public sealed record ChannelDefinition(
    string ContentType,
    string Mode,
    int? YearFrom,
    int? YearBefore,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    IReadOnlyList<string> ItemIds,
    string AirOrder)
{
    public const int DefaultSimilarCount = 30;

    /// <summary>Age ratings such as PG or TV-14. Any of them matches.</summary>
    public IReadOnlyList<string> Ratings { get; init; } = [];

    /// <summary>The lowest community score (0-10) to include.</summary>
    public double? MinScore { get; init; }

    /// <summary>Length limits in minutes: a movie's runtime, or a show's typical episode.</summary>
    public int? MinMinutes { get; init; }

    public int? MaxMinutes { get; init; }

    /// <summary>Only titles added recently; for a show, when its newest episode arrived.</summary>
    public int? AddedWithinDays { get; init; }

    /// <summary>Words or phrases found in a title's Jellyfin tags, name or description. Any of them matches.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Directors, creators or lead actors. Any of them matches.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>For the Similar mode: how many titles to add to the chosen ones.</summary>
    public int? SimilarCount { get; init; }

    /// <summary>Identifies channels that would play the same content, whatever order it airs in.</summary>
    public string Signature => string.Join("|",
        ContentType, Mode, YearFrom, YearBefore,
        Joined(Genres), Joined(Studios),
        string.Join(",", ItemIds.Order(StringComparer.Ordinal)),
        Joined(Ratings), MinScore, MinMinutes, MaxMinutes, AddedWithinDays,
        Joined(Keywords), Joined(People), SimilarCount);

    private static string Joined(IEnumerable<string> values) =>
        string.Join(",", values.Select(v => v.ToLowerInvariant()).Order(StringComparer.Ordinal));
}

public sealed record Channel(
    long Id, int Number, string Name, ChannelDefinition Definition, long? LogoVersion, string? Color, bool ColorCustom)
{
    /// <summary>Changes whenever the image does, so browsers can cache each version for good.</summary>
    public string? LogoUrl => LogoVersion is { } version ? $"/api/channels/{Id}/logo?v={version}" : null;
}

/// <summary>Tests catalog titles against a channel's definition.</summary>
public sealed class ChannelMatcher(ChannelDefinition definition)
{
    private readonly HashSet<string> _genres = definition.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _studios = definition.Studios.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ratings = definition.Ratings.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _people = definition.People.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Keyword, Regex Pattern)> _keywords = definition.Keywords
        .Select(k => (k, new Regex($@"\b{Regex.Escape(k)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        .ToList();

    private readonly DateTimeOffset? _addedSince = definition.AddedWithinDays is { } days
        ? DateTimeOffset.UtcNow.AddDays(-days)
        : null;

    public IReadOnlyList<CatalogTitle> Select(CatalogSnapshot catalog)
    {
        var shows = definition.ContentType == ChannelContent.Shows;
        var titles = shows ? catalog.Shows : catalog.Movies;
        switch (definition.Mode)
        {
            case ChannelMode.Filter:
                return titles.Where(MatchesFilter).ToList();

            case ChannelMode.Similar:
            {
                // Best matches first, then the chosen titles, so previews show what was found.
                var byId = titles.ToDictionary(t => t.Id);
                var similar = catalog.Similarity(shows)
                    .MoreLike(definition.ItemIds, definition.SimilarCount ?? ChannelDefinition.DefaultSimilarCount);
                return similar.Select(s => byId[s.Id])
                    .Concat(definition.ItemIds.Where(byId.ContainsKey).Select(id => byId[id]))
                    .ToList();
            }
        }

        var ids = definition.Mode == ChannelMode.Pick
            ? definition.ItemIds.ToHashSet()
            : definition.ItemIds
                .SelectMany(id => catalog.CollectionMembers.GetValueOrDefault(id) ?? [])
                .ToHashSet();
        return titles.Where(t => ids.Contains(t.Id)).ToList();
    }

    private bool MatchesFilter(CatalogTitle title) =>
        (definition.YearFrom is not { } from || title.Year >= from) &&
        (definition.YearBefore is not { } before || title.Year < before) &&
        (_genres.Count == 0 || title.Genres.Any(_genres.Contains)) &&
        (_studios.Count == 0 || title.Studios.Any(_studios.Contains)) &&
        (_ratings.Count == 0 || (title.Rating is { } rating && _ratings.Contains(rating))) &&
        (definition.MinScore is not { } score || title.Score >= score) &&
        (definition.MinMinutes is not { } shortest || title.AverageMinutes >= shortest) &&
        (definition.MaxMinutes is not { } longest || title.AverageMinutes <= longest) &&
        (_addedSince is not { } since || title.AddedAt >= since) &&
        (_people.Count == 0 || title.People.Any(p => _people.Contains(p.Name))) &&
        (_keywords.Count == 0 || _keywords.Any(k => HasKeyword(title, k.Keyword, k.Pattern)));

    // Jellyfin's tags are usually TMDB keywords, so they're the best signal; the name and description fill in.
    private static bool HasKeyword(CatalogTitle title, string keyword, Regex pattern) =>
        title.Tags.Any(t => t.Equals(keyword, StringComparison.OrdinalIgnoreCase) || pattern.IsMatch(t)) ||
        pattern.IsMatch(title.Title) ||
        (title.Overview is { } overview && pattern.IsMatch(overview));
}
