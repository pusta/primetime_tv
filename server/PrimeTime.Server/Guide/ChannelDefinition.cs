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
    /// <summary>Identifies channels that would play the same content, whatever order it airs in.</summary>
    public string Signature => string.Join("|",
        ContentType, Mode, YearFrom, YearBefore,
        string.Join(",", Genres.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(",", Studios.Order(StringComparer.OrdinalIgnoreCase)),
        string.Join(",", ItemIds.Order(StringComparer.Ordinal)));
}

public sealed record Channel(long Id, int Number, string Name, ChannelDefinition Definition);

/// <summary>Tests catalog titles against a channel's definition.</summary>
public sealed class ChannelMatcher(ChannelDefinition definition)
{
    private readonly HashSet<string> _genres = definition.Genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _studios = definition.Studios.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CatalogTitle> Select(CatalogSnapshot catalog)
    {
        var titles = definition.ContentType == ChannelContent.Movies ? catalog.Movies : catalog.Shows;
        if (definition.Mode == ChannelMode.Filter)
        {
            return titles.Where(MatchesFilter).ToList();
        }

        var ids = definition.Mode == ChannelMode.Pick
            ? definition.ItemIds.ToHashSet()
            : definition.ItemIds
                .SelectMany(id => catalog.CollectionMembers.GetValueOrDefault(id) ?? [])
                .ToHashSet();
        return titles.Where(t => ids.Contains(t.Id)).ToList();
    }

    private bool MatchesFilter(CatalogTitle title)
    {
        if (definition.YearFrom is { } from && !(title.Year >= from))
        {
            return false;
        }

        if (definition.YearBefore is { } before && !(title.Year < before))
        {
            return false;
        }

        return (_genres.Count == 0 || title.Genres.Any(_genres.Contains)) &&
               (_studios.Count == 0 || title.Studios.Any(_studios.Contains));
    }
}
