using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PrimeTime.Server.Data;

/// <summary>
/// A movie or show that can be scheduled. RuntimeSeconds is the total for a show.
/// Released falls back to the start of the title's year.
/// </summary>
public sealed record CatalogTitle(
    string Id,
    string Title,
    int? Year,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios,
    long RuntimeSeconds,
    int EpisodeCount,
    DateTimeOffset? Released);

public sealed record CatalogEpisode(
    string Id, int Season, int Episode, string Title, long RuntimeSeconds, DateTimeOffset? AirDate);

public sealed record CatalogSnapshot(
    IReadOnlyList<CatalogTitle> Movies,
    IReadOnlyList<CatalogTitle> Shows,
    IReadOnlyDictionary<string, IReadOnlyList<CatalogEpisode>> EpisodesByShow,
    IReadOnlyList<StoredCollection> Collections,
    IReadOnlyDictionary<string, HashSet<string>> CollectionMembers);

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
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT show_id, id, season_number, episode_number, title, runtime_seconds, premiere_date
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
            }
        }

        var movies = new List<CatalogTitle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, year, genres, studios, runtime_seconds, premiere_date
                FROM movies WHERE runtime_seconds > 0
                ORDER BY COALESCE(sort_title, title) COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var year = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                movies.Add(new CatalogTitle(
                    reader.GetString(0), reader.GetString(1), year,
                    ReadList(reader.GetString(3)), ReadList(reader.GetString(4)),
                    reader.GetInt64(5), EpisodeCount: 0, ReadDate(reader, 6, year)));
            }
        }

        var shows = new List<CatalogTitle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, title, year, genres, studios, premiere_date
                FROM shows ORDER BY COALESCE(sort_title, title) COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (episodes.TryGetValue(id, out var list))
                {
                    var year = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                    shows.Add(new CatalogTitle(
                        id, reader.GetString(1), year,
                        ReadList(reader.GetString(3)), ReadList(reader.GetString(4)),
                        list.Sum(e => e.RuntimeSeconds), list.Count, ReadDate(reader, 5, year)));
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
}
