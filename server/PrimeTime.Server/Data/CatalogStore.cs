using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrimeTime.Server.Guide;

namespace PrimeTime.Server.Data;

/// <summary>
/// A movie or show that can be scheduled. RuntimeSeconds is the total for a show, and
/// AverageMinutes is a movie's length or a show's typical episode. Released falls back to the
/// start of the title's year. A show counts as added when its newest episode was.
/// </summary>
public sealed record CatalogTitle(
    string Id,
    string Title,
    int? Year,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    long RuntimeSeconds,
    int EpisodeCount,
    DateTimeOffset? Released,
    IReadOnlyList<string> Tags,
    IReadOnlyList<StoredPerson> People,
    string? Rating,
    double? Score,
    DateTimeOffset? AddedAt,
    string? Overview,
    int AverageMinutes);

public sealed record CatalogEpisode(
    string Id, int Season, int Episode, string Title, long RuntimeSeconds, DateTimeOffset? AirDate);

public sealed record CatalogSnapshot(
    IReadOnlyList<CatalogTitle> Movies,
    IReadOnlyList<CatalogTitle> Shows,
    IReadOnlyDictionary<string, IReadOnlyList<CatalogEpisode>> EpisodesByShow,
    IReadOnlyList<StoredCollection> Collections,
    IReadOnlyDictionary<string, HashSet<string>> CollectionMembers)
{
    private readonly Dictionary<bool, SimilarityIndex> _similarity = [];

    /// <summary>Built the first time a channel needs it, then shared by everything using this snapshot.</summary>
    public SimilarityIndex Similarity(bool shows)
    {
        lock (_similarity)
        {
            if (!_similarity.TryGetValue(shows, out var index))
            {
                _similarity[shows] = index = SimilarityIndex.Build(shows ? Shows : Movies);
            }

            return index;
        }
    }
}

/// <summary>
/// Reads what the index can actually put on air: items with a known runtime, numbered
/// episodes outside season 0 (specials), and shows with at least one such episode.
/// </summary>
public sealed class CatalogStore(Database database, CollectionStore collections)
{
    public CatalogSnapshot Load()
    {
        using var connection = database.Open();

        var episodes = new Dictionary<string, List<CatalogEpisode>>();
        var newestEpisode = new Dictionary<string, DateTimeOffset>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT show_id, id, season_number, episode_number, title, runtime_seconds, premiere_date, date_added
                FROM episodes
                WHERE season_number >= 1 AND episode_number IS NOT NULL AND runtime_seconds > 0
                ORDER BY show_id, season_number, episode_number;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var showId = reader.GetString(0);
                if (!episodes.TryGetValue(showId, out var list))
                {
                    episodes[showId] = list = [];
                }

                list.Add(new CatalogEpisode(
                    reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetInt64(5), ReadDate(reader, 6, null)));
                if (ReadDate(reader, 7, null) is { } added &&
                    (!newestEpisode.TryGetValue(showId, out var newest) || added > newest))
                {
                    newestEpisode[showId] = added;
                }
            }
        }

        var movies = new List<CatalogTitle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, year, genres, studios, runtime_seconds, premiere_date,
                       tags, people, official_rating, community_rating, date_added, overview
                FROM movies WHERE runtime_seconds > 0
                ORDER BY COALESCE(sort_title, title) COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var year = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                var runtime = reader.GetInt64(5);
                movies.Add(new CatalogTitle(
                    reader.GetString(0), reader.GetString(1), year,
                    ReadList(reader.GetString(3)), ReadList(reader.GetString(4)),
                    runtime, EpisodeCount: 0, ReadDate(reader, 6, year),
                    ReadList(reader.GetString(7)), ReadPeople(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    ReadDate(reader, 11, null),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    (int)Math.Round(runtime / 60.0)));
            }
        }

        var shows = new List<CatalogTitle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, year, genres, studios, premiere_date,
                       tags, people, official_rating, community_rating, date_added, overview
                FROM shows ORDER BY COALESCE(sort_title, title) COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (episodes.TryGetValue(id, out var list))
                {
                    var year = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                    var total = list.Sum(e => e.RuntimeSeconds);
                    var showAdded = ReadDate(reader, 10, null);
                    var added = newestEpisode.TryGetValue(id, out var newest) && (showAdded is null || newest > showAdded)
                        ? newest
                        : showAdded;
                    shows.Add(new CatalogTitle(
                        id, reader.GetString(1), year,
                        ReadList(reader.GetString(3)), ReadList(reader.GetString(4)),
                        total, list.Count, ReadDate(reader, 5, year),
                        ReadList(reader.GetString(6)), ReadPeople(reader.GetString(7)),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.IsDBNull(9) ? null : reader.GetDouble(9),
                        added,
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        (int)Math.Round(total / 60.0 / list.Count)));
                }
            }
        }

        return new CatalogSnapshot(
            movies, shows,
            episodes.ToDictionary(p => p.Key, p => (IReadOnlyList<CatalogEpisode>)p.Value),
            collections.List(),
            collections.Members());
    }

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int column, int? fallbackYear)
    {
        if (!reader.IsDBNull(column) && DateTimeOffset.TryParse(reader.GetString(column), out var date))
        {
            return date;
        }

        return fallbackYear is >= 1 and <= 9999 ? new DateTimeOffset(fallbackYear.Value, 1, 1, 0, 0, 0, TimeSpan.Zero) : null;
    }

    private static List<string> ReadList(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static List<StoredPerson> ReadPeople(string json) =>
        JsonSerializer.Deserialize<List<StoredPerson>>(json) ?? [];
}
