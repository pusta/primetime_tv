using System.Globalization;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

public sealed record ChannelSuggestion(string Group, string Name, string Detail, ChannelDefinition Definition);

/// <summary>Proposes channels from what's in the library, leaving out any that already exist.</summary>
public static class ChannelSuggestions
{
    private const int MinMovies = 8;
    private const int MinShows = 3;
    private const int MinSeasonalMovies = 4;
    private const int MinMarathonEpisodes = 40;
    private const int MinCollectionTitles = 2;
    private const int MaxCollections = 12;
    private const int MinDirectorMovies = 5;
    private const int MinActorMovies = 6;
    private const int RecentDays = 30;

    // A tag on more than this share of titles says nothing about any of them.
    private const double MaxTagShare = 0.35;

    // Tags that describe the release or a viewer's mood rather than what a title is about.
    private static readonly HashSet<string> UnhelpfulTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "aftercreditsstinger", "duringcreditsstinger", "woman director", "sequel", "prequel", "remake",
        "spin off", "amused", "excited", "admiring", "cliché", "depressing", "dramatic", "playful",
        "anxious", "approving", "disheartening", "frustrated", "inspirational", "suspenseful",
    };

    /// <summary>True for tags that make poor themes: very common, about the release, a mood, or a place.</summary>
    public static bool IsUnhelpfulTag(string tag, int titleCount) =>
        UnhelpfulTags.Contains(tag) || tag.StartsWith("parents-", StringComparison.OrdinalIgnoreCase) ||
        tag.Contains(',') || tag.Length > 40 || titleCount == 0;

    public static IReadOnlyList<ChannelSuggestion> Build(CatalogSnapshot catalog, IEnumerable<Channel> existing)
    {
        var taken = existing.Select(c => c.Definition.Signature).ToHashSet();
        var candidates = new List<Candidate>();

        AddCollections(catalog, candidates);

        var movies = catalog.Movies;
        var shows = catalog.Shows;

        foreach (var (genre, _) in TopValues(movies, t => t.Genres, MinMovies, 6))
        {
            candidates.Add(new("Movies by genre", $"{genre} Movies", Movies() with { Genres = [genre] }));
        }

        var decades = movies.Where(m => m.Year is not null)
            .GroupBy(m => m.Year!.Value / 10 * 10)
            .Where(g => g.Count() >= MinMovies)
            .OrderBy(g => g.Key)
            .ToList();
        foreach (var decade in decades)
        {
            candidates.Add(new("Movies by decade", $"{decade.Key}s Movies", Decade(decade.Key)));
        }

        // The strongest genres within each decade, like 80s Action.
        var combos = decades
            .SelectMany(d => TopValues(d.ToList(), t => t.Genres, MinMovies, 2)
                .Select(g => (Decade: d.Key, Genre: g.Value, g.Count)))
            .OrderByDescending(c => c.Count)
            .Take(8);
        foreach (var (decade, genre, _) in combos)
        {
            candidates.Add(new("Decades and genres", $"{decade % 100:00}s {genre}", Decade(decade) with { Genres = [genre] }));
        }

        foreach (var tag in Themes(movies, MinMovies, 8))
        {
            candidates.Add(new("Movie themes", ThemeName(tag, "Movies"), Movies() with { Keywords = [tag] }));
        }

        foreach (var (name, _) in TopPeople(movies, role => role is "Director" or "Creator", MinDirectorMovies, 6))
        {
            candidates.Add(new("Directors", $"{name} Movies", Movies() with { People = [name] }, MinDirectorMovies));
        }

        foreach (var (name, _) in TopPeople(movies, role => role == "Actor", MinActorMovies, 6))
        {
            candidates.Add(new("Stars", $"{name} Movies", Movies() with { People = [name] }, MinActorMovies));
        }

        candidates.Add(new("Family and kids", "Family Movies", Movies() with { Ratings = ["G", "PG", "TV-G", "TV-Y", "TV-Y7"] }));
        candidates.Add(new("Family and kids", "Kids TV", Shows() with { Ratings = ["TV-Y", "TV-Y7", "TV-G"] }, MinShows));

        candidates.Add(new("Best rated", "Critics' Picks", Movies() with { MinScore = 7.5 }));
        candidates.Add(new("Best rated", "Top-Rated Shows", Shows() with { MinScore = 8 }, MinShows));

        candidates.Add(new("By length", "Quick Watches", Movies() with { MaxMinutes = 95 }));
        candidates.Add(new("By length", "Epic Movies", Movies() with { MinMinutes = 150 }));
        candidates.Add(new("By length", "Half-Hour Shows", Shows() with { MaxMinutes = 35 }, MinShows));

        candidates.Add(new("Recently added", "New Arrivals", Movies() with { AddedWithinDays = RecentDays }));
        candidates.Add(new("Recently added", "New Episodes", Shows() with { AddedWithinDays = RecentDays }, MinShows));

        candidates.Add(new("Seasonal", "Christmas Movies", Movies() with { Keywords = ["christmas"] }, MinSeasonalMovies));
        candidates.Add(new("Seasonal", "Halloween Movies", Movies() with { Keywords = ["halloween"] }, MinSeasonalMovies));

        foreach (var (genre, _) in TopValues(shows, t => t.Genres, MinShows, 6))
        {
            candidates.Add(new("Shows by genre", $"{genre} Shows", Shows() with { Genres = [genre] }, MinShows));
        }

        foreach (var (network, _) in TopValues(shows, t => t.Studios, MinShows, 4))
        {
            candidates.Add(new("Shows by network", network, Shows() with { Studios = [network] }, MinShows));
        }

        foreach (var tag in Themes(shows, MinShows, 4))
        {
            candidates.Add(new("Show themes", ThemeName(tag, "Shows"), Shows() with { Keywords = [tag] }, MinShows));
        }

        foreach (var show in shows.Where(s => s.EpisodeCount >= MinMarathonEpisodes).OrderByDescending(s => s.EpisodeCount).Take(4))
        {
            candidates.Add(new("Marathons", $"{show.Title} Marathon",
                new ChannelDefinition(ChannelContent.Shows, ChannelMode.Pick, null, null, [], [], [show.Id], AirOrder.OldestFirst), 1));
        }

        // Every candidate is checked against the library with the same rules a saved channel uses,
        // so the counts shown are exactly what the channel would play.
        var suggestions = new List<ChannelSuggestion>();
        foreach (var candidate in candidates)
        {
            var signature = candidate.Definition.Signature;
            if (!taken.Add(signature))
            {
                continue;
            }

            var titles = new ChannelMatcher(candidate.Definition).Select(catalog);
            var minimum = candidate.Minimum ?? MinMovies;
            if (titles.Count < minimum)
            {
                continue;
            }

            var detail = candidate.Definition.Mode == ChannelMode.Pick
                ? Count(titles.Sum(t => t.EpisodeCount), "episode")
                : candidate.Definition.ContentType == ChannelContent.Shows
                    ? Count(titles.Count, "show")
                    : Count(titles.Count, "movie");
            suggestions.Add(new ChannelSuggestion(candidate.Group, candidate.Name, detail, candidate.Definition));
        }

        return suggestions;
    }

    private sealed record Candidate(string Group, string Name, ChannelDefinition Definition, int? Minimum = null);

    private static void AddCollections(CatalogSnapshot catalog, List<Candidate> candidates)
    {
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
            var content = movies >= shows ? ChannelContent.Movies : ChannelContent.Shows;
            candidates.Add(new("Your collections", collection.Name,
                new ChannelDefinition(content, ChannelMode.Collection, null, null, [], [], [collection.Id],
                    AirOrder.DefaultFor(content)), MinCollectionTitles));
        }
    }

    /// <summary>The most common useful tags, skipping ones that are just a genre under another name.</summary>
    private static IEnumerable<string> Themes(IReadOnlyList<CatalogTitle> titles, int minimum, int take)
    {
        var genres = titles.SelectMany(t => t.Genres).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maximum = (int)(titles.Count * MaxTagShare);
        return titles.SelectMany(t => t.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(tag => !IsUnhelpfulTag(tag, titles.Count) && !genres.Contains(tag))
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key, Count: g.Count()))
            .Where(t => t.Count >= minimum && t.Count <= maximum)
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(t => t.Tag);
    }

    /// <summary>
    /// "superhero" becomes "Superhero Movies", "based on comic" becomes "Comic Adaptations", and
    /// "marvel cinematic universe (mcu)" drops its bracketed abbreviation.
    /// </summary>
    private static string ThemeName(string tag, string noun)
    {
        var bracket = tag.IndexOf('(');
        var plain = (bracket > 0 ? tag[..bracket] : tag).Trim();
        var text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(plain.ToLowerInvariant());
        if (tag.StartsWith("based on ", StringComparison.OrdinalIgnoreCase))
        {
            var source = text["Based On ".Length..];
            return (source == "Novel Or Book" ? "Book" : source) + " Adaptations";
        }

        // Some tags already name a kind of show.
        if (noun == "Shows" && text.EndsWith("series", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return text.Equals("Sitcom", StringComparison.OrdinalIgnoreCase) ? "Sitcoms" : $"{text} {noun}";
    }

    private static IEnumerable<(string Name, int Count)> TopPeople(
        IReadOnlyList<CatalogTitle> titles, Func<string, bool> role, int minimum, int take) =>
        titles.SelectMany(t => t.People.Where(p => role(p.Role)).Select(p => p.Name).Distinct())
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .Where(p => p.Count >= minimum)
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take);

    private static IEnumerable<(string Value, int Count)> TopValues(
        IEnumerable<CatalogTitle> titles, Func<CatalogTitle, IEnumerable<string>> selector, int minimum, int take) =>
        titles.SelectMany(selector)
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Value: g.Key, Count: g.Count()))
            .Where(v => v.Count >= minimum)
            .OrderByDescending(v => v.Count)
            .ThenBy(v => v.Value, StringComparer.OrdinalIgnoreCase)
            .Take(take);

    private static ChannelDefinition Movies() => Filter(ChannelContent.Movies);

    private static ChannelDefinition Shows() => Filter(ChannelContent.Shows);

    private static ChannelDefinition Decade(int decade) => Movies() with { YearFrom = decade, YearBefore = decade + 10 };

    private static ChannelDefinition Filter(string content) =>
        new(content, ChannelMode.Filter, null, null, [], [], [], AirOrder.DefaultFor(content));

    private static string Count(int value, string noun) => $"{value:N0} {noun}{(value == 1 ? "" : "s")}";
}
