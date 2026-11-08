using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

public sealed record ChannelSuggestion(string Group, string Name, string Detail, ChannelDefinition Definition);

/// <summary>Proposes channels from what's in the library, leaving out any that already exist.</summary>
public static class ChannelSuggestions
{
    private const int MinMovies = 8;
    private const int MinShows = 3;
    private const int MinMarathonEpisodes = 40;
    private const int MinCollectionTitles = 2;
    private const int MaxCollections = 12;

    public static IReadOnlyList<ChannelSuggestion> Build(CatalogSnapshot catalog, IEnumerable<Channel> existing)
    {
        var taken = existing.Select(c => c.Definition.Signature).ToHashSet();
        var suggestions = new List<ChannelSuggestion>();

        var collections = catalog.Collections
            .Select(c => (Collection: c, Members: catalog.CollectionMembers.GetValueOrDefault(c.Id) ?? []))
            .Select(c => (c.Collection,
                Movies: catalog.Movies.Count(m => c.Members.Contains(m.Id)),
                Shows: catalog.Shows.Count(s => c.Members.Contains(s.Id))))
            .Where(c => Math.Max(c.Movies, c.Shows) >= MinCollectionTitles)
            .OrderByDescending(c => Math.Max(c.Movies, c.Shows))
            .Take(MaxCollections);
        foreach (var (collection, movies, shows) in collections)
        {
            // A channel plays one kind of content, so suggest whichever the collection mostly holds.
            var (content, detail) = movies >= shows
                ? (ChannelContent.Movies, Count(movies, "movie"))
                : (ChannelContent.Shows, Count(shows, "show"));
            suggestions.Add(new("Your collections", collection.Name, detail,
                new ChannelDefinition(content, ChannelMode.Collection, null, null, [], [], [collection.Id], AirOrder.DefaultFor(content))));
        }

        foreach (var (genre, count) in TopValues(catalog.Movies, t => t.Genres, MinMovies, 6))
        {
            suggestions.Add(new("Movies by genre", $"{genre} Movies", Count(count, "movie"),
                Filter(ChannelContent.Movies, genres: [genre])));
        }

        var decades = catalog.Movies
            .Where(m => m.Year is not null)
            .GroupBy(m => m.Year!.Value / 10 * 10)
            .Where(g => g.Count() >= MinMovies)
            .OrderBy(g => g.Key);
        foreach (var decade in decades)
        {
            suggestions.Add(new("Movies by decade", $"{decade.Key}s Movies", Count(decade.Count(), "movie"),
                Filter(ChannelContent.Movies, from: decade.Key, before: decade.Key + 10)));
        }

        foreach (var (genre, count) in TopValues(catalog.Shows, t => t.Genres, MinShows, 6))
        {
            suggestions.Add(new("Shows by genre", $"{genre} Shows", Count(count, "show"),
                Filter(ChannelContent.Shows, genres: [genre])));
        }

        foreach (var (network, count) in TopValues(catalog.Shows, t => t.Studios, MinShows, 4))
        {
            suggestions.Add(new("Shows by network", network, Count(count, "show"),
                Filter(ChannelContent.Shows, studios: [network])));
        }

        var marathons = catalog.Shows
            .Where(s => s.EpisodeCount >= MinMarathonEpisodes)
            .OrderByDescending(s => s.EpisodeCount)
            .Take(4);
        foreach (var show in marathons)
        {
            suggestions.Add(new("Marathons", $"{show.Title} Marathon", Count(show.EpisodeCount, "episode"),
                new ChannelDefinition(ChannelContent.Shows, ChannelMode.Pick, null, null, [], [], [show.Id], AirOrder.OldestFirst)));
        }

        return suggestions.Where(s => !taken.Contains(s.Definition.Signature)).ToList();
    }

    private static IEnumerable<(string Value, int Count)> TopValues(
        IEnumerable<CatalogTitle> titles, Func<CatalogTitle, IEnumerable<string>> selector, int minimum, int take) =>
        titles.SelectMany(selector)
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Value: g.Key, Count: g.Count()))
            .Where(v => v.Count >= minimum)
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .Take(take);

    private static ChannelDefinition Filter(
        string content, int? from = null, int? before = null,
        IReadOnlyList<string>? genres = null, IReadOnlyList<string>? studios = null) =>
        new(content, ChannelMode.Filter, from, before, genres ?? [], studios ?? [], [], AirOrder.DefaultFor(content));

    private static string Count(int value, string noun) => $"{value:N0} {noun}{(value == 1 ? "" : "s")}";
}
