using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrimeTime.Server.Jellyfin;

namespace PrimeTime.Server.Data;

/// <summary>
/// The local index of movies, shows and episodes. Each row remembers the task run that
/// first and last saw it, which is how a scan counts additions and finds removals.
/// </summary>
public sealed class MediaIndexStore(Database database)
{
    private const long TicksPerSecond = 10_000_000;

    public void UpsertMovies(string libraryId, long runId, IReadOnlyList<JellyfinMediaItem> items)
    {
        const string sql = """
            INSERT INTO movies
                (id, library_id, title, sort_title, year, overview, runtime_seconds, genres, studios, tags,
                 official_rating, community_rating, premiere_date, date_added, provider_ids,
                 first_seen_run_id, last_seen_run_id)
            VALUES
                ($id, $libraryId, $title, $sortTitle, $year, $overview, $runtime, $genres, $studios, $tags,
                 $officialRating, $communityRating, $premiereDate, $dateAdded, $providerIds,
                 $runId, $runId)
            ON CONFLICT (id) DO UPDATE SET
                library_id = excluded.library_id, title = excluded.title, sort_title = excluded.sort_title,
                year = excluded.year, overview = excluded.overview, runtime_seconds = excluded.runtime_seconds,
                genres = excluded.genres, studios = excluded.studios, tags = excluded.tags,
                official_rating = excluded.official_rating, community_rating = excluded.community_rating,
                premiere_date = excluded.premiere_date, date_added = excluded.date_added,
                provider_ids = excluded.provider_ids, last_seen_run_id = excluded.last_seen_run_id;
            """;

        WriteBatch(sql, libraryId, runId, items, (command, item) =>
        {
            AddTitleFields(command, item);
            command.Parameters.AddWithValue("$runtime", Nullable(RuntimeSeconds(item)));
        });
    }

    public void UpsertShows(string libraryId, long runId, IReadOnlyList<JellyfinMediaItem> items)
    {
        const string sql = """
            INSERT INTO shows
                (id, library_id, title, sort_title, year, overview, status, genres, studios, tags,
                 official_rating, community_rating, premiere_date, date_added, provider_ids,
                 first_seen_run_id, last_seen_run_id)
            VALUES
                ($id, $libraryId, $title, $sortTitle, $year, $overview, $status, $genres, $studios, $tags,
                 $officialRating, $communityRating, $premiereDate, $dateAdded, $providerIds,
                 $runId, $runId)
            ON CONFLICT (id) DO UPDATE SET
                library_id = excluded.library_id, title = excluded.title, sort_title = excluded.sort_title,
                year = excluded.year, overview = excluded.overview, status = excluded.status,
                genres = excluded.genres, studios = excluded.studios, tags = excluded.tags,
                official_rating = excluded.official_rating, community_rating = excluded.community_rating,
                premiere_date = excluded.premiere_date, date_added = excluded.date_added,
                provider_ids = excluded.provider_ids, last_seen_run_id = excluded.last_seen_run_id;
            """;

        WriteBatch(sql, libraryId, runId, items, (command, item) =>
        {
            AddTitleFields(command, item);
            command.Parameters.AddWithValue("$status", Nullable(item.Status));
        });
    }

    public void UpsertEpisodes(string libraryId, long runId, IReadOnlyList<JellyfinMediaItem> items)
    {
        const string sql = """
            INSERT INTO episodes
                (id, show_id, library_id, season_number, episode_number, title, overview, runtime_seconds,
                 premiere_date, date_added, provider_ids, first_seen_run_id, last_seen_run_id)
            VALUES
                ($id, $showId, $libraryId, $season, $episode, $title, $overview, $runtime,
                 $premiereDate, $dateAdded, $providerIds, $runId, $runId)
            ON CONFLICT (id) DO UPDATE SET
                show_id = excluded.show_id, library_id = excluded.library_id,
                season_number = excluded.season_number, episode_number = excluded.episode_number,
                title = excluded.title, overview = excluded.overview, runtime_seconds = excluded.runtime_seconds,
                premiere_date = excluded.premiere_date, date_added = excluded.date_added,
                provider_ids = excluded.provider_ids, last_seen_run_id = excluded.last_seen_run_id;
            """;

        WriteBatch(sql, libraryId, runId, items, (command, item) =>
        {
            command.Parameters.AddWithValue("$showId", item.SeriesId!);
            command.Parameters.AddWithValue("$season", Nullable(item.ParentIndexNumber));
            command.Parameters.AddWithValue("$episode", Nullable(item.IndexNumber));
            command.Parameters.AddWithValue("$title", item.Name ?? "(untitled)");
            command.Parameters.AddWithValue("$overview", Nullable(item.Overview));
            command.Parameters.AddWithValue("$runtime", Nullable(RuntimeSeconds(item)));
            command.Parameters.AddWithValue("$premiereDate", Nullable(item.PremiereDate));
            command.Parameters.AddWithValue("$dateAdded", Nullable(item.DateCreated));
            command.Parameters.AddWithValue("$providerIds", JsonSerializer.Serialize(item.ProviderIds ?? []));
        });
    }

    /// <summary>Items first seen during this run.</summary>
    public int CountAdded(string libraryId, long runId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM movies WHERE library_id = $libraryId AND first_seen_run_id = $runId)
                 + (SELECT COUNT(*) FROM shows WHERE library_id = $libraryId AND first_seen_run_id = $runId)
                 + (SELECT COUNT(*) FROM episodes WHERE library_id = $libraryId AND first_seen_run_id = $runId);
            """;
        command.Parameters.AddWithValue("$libraryId", libraryId);
        command.Parameters.AddWithValue("$runId", runId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Deletes items this run didn't see. Only call after a complete scan of the library.</summary>
    public int RemoveUnseen(string libraryId, long runId)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        var removed = 0;

        // Episodes first, so ones removed along with their show are still counted.
        foreach (var table in new[] { "episodes", "shows", "movies" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"DELETE FROM {table} WHERE library_id = $libraryId AND last_seen_run_id <> $runId;";
            command.Parameters.AddWithValue("$libraryId", libraryId);
            command.Parameters.AddWithValue("$runId", runId);
            removed += command.ExecuteNonQuery();
        }

        tx.Commit();
        return removed;
    }

    private void WriteBatch(
        string sql, string libraryId, long runId, IReadOnlyList<JellyfinMediaItem> items,
        Action<SqliteCommand, JellyfinMediaItem> bind)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;

        foreach (var item in items)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", item.Id!);
            command.Parameters.AddWithValue("$libraryId", libraryId);
            command.Parameters.AddWithValue("$runId", runId);
            bind(command, item);
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void AddTitleFields(SqliteCommand command, JellyfinMediaItem item)
    {
        command.Parameters.AddWithValue("$title", item.Name ?? "(untitled)");
        command.Parameters.AddWithValue("$sortTitle", Nullable(item.SortName));
        command.Parameters.AddWithValue("$year", Nullable(item.ProductionYear));
        command.Parameters.AddWithValue("$overview", Nullable(item.Overview));
        command.Parameters.AddWithValue("$genres", JsonSerializer.Serialize(item.Genres ?? []));
        command.Parameters.AddWithValue("$studios",
            JsonSerializer.Serialize((item.Studios ?? []).Select(s => s.Name).Where(n => n is not null)));
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(item.Tags ?? []));
        command.Parameters.AddWithValue("$officialRating", Nullable(item.OfficialRating));
        command.Parameters.AddWithValue("$communityRating", Nullable(item.CommunityRating));
        command.Parameters.AddWithValue("$premiereDate", Nullable(item.PremiereDate));
        command.Parameters.AddWithValue("$dateAdded", Nullable(item.DateCreated));
        command.Parameters.AddWithValue("$providerIds", JsonSerializer.Serialize(item.ProviderIds ?? []));
    }

    private static long? RuntimeSeconds(JellyfinMediaItem item) => item.RunTimeTicks / TicksPerSecond;

    private static object Nullable<T>(T? value) => (object?)value ?? DBNull.Value;
}
