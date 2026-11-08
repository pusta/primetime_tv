using System.Text.Json;
using PrimeTime.Server.Jellyfin;

namespace PrimeTime.Server.Data;

public sealed record StoredLibrary(
    string Id,
    string Name,
    string CollectionType,
    IReadOnlyList<string> Locations,
    bool Selected,
    int Movies,
    int Shows,
    int Episodes,
    DateTimeOffset? LastScannedAt,
    string? LastScanError);

public static class LibraryTypes
{
    public const string Movies = "movies";
    public const string Shows = "tvshows";
    public const string Mixed = "mixed";
    public const string Collections = "boxsets";

    /// <summary>Only these Jellyfin library types can hold movies and shows.</summary>
    public static bool HoldsMedia(string collectionType) =>
        collectionType is Movies or Shows or Mixed;

    /// <summary>What the admin can choose to include: media libraries and the Collections folder.</summary>
    public static bool IsSupported(string collectionType) =>
        HoldsMedia(collectionType) || collectionType == Collections;
}

/// <summary>The Jellyfin libraries PrimeTime knows about and which ones the admin included.</summary>
public sealed class LibraryStore(Database database)
{
    public IReadOnlyList<StoredLibrary> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.id, l.name, l.collection_type, l.locations, l.selected,
                   (SELECT COUNT(*) FROM movies m WHERE m.library_id = l.id),
                   (SELECT COUNT(*) FROM shows s WHERE s.library_id = l.id),
                   (SELECT COUNT(*) FROM episodes e WHERE e.library_id = l.id),
                   l.last_scanned_at, l.last_scan_error
            FROM libraries l
            ORDER BY l.name COLLATE NOCASE;
            """;

        var result = new List<StoredLibrary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StoredLibrary(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                CollectionType: reader.GetString(2),
                Locations: JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
                Selected: reader.GetInt64(4) == 1,
                Movies: reader.GetInt32(5),
                Shows: reader.GetInt32(6),
                Episodes: reader.GetInt32(7),
                LastScannedAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                LastScanError: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return result;
    }

    public bool AnySelected()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM libraries WHERE selected = 1);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    /// <summary>Adds libraries that are new on the server (not selected) and refreshes the rest.</summary>
    public void Upsert(IReadOnlyList<LibrarySummary> libraries)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        foreach (var library in libraries.Where(l => l.Id is not null))
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO libraries (id, name, collection_type, locations)
                VALUES ($id, $name, $type, $locations)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    collection_type = excluded.collection_type,
                    locations = excluded.locations;
                """;
            command.Parameters.AddWithValue("$id", library.Id!);
            command.Parameters.AddWithValue("$name", library.Name);
            command.Parameters.AddWithValue("$type", library.CollectionType);
            command.Parameters.AddWithValue("$locations", JsonSerializer.Serialize(library.Locations));
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Forgets libraries that no longer exist on the server, and their indexed items.</summary>
    public void DeleteExcept(IReadOnlySet<string> keepIds)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();
        foreach (var library in List().Where(l => !keepIds.Contains(l.Id)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "DELETE FROM libraries WHERE id = $id;";
            command.Parameters.AddWithValue("$id", library.Id);
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>Applies the admin's choice and removes indexed items from libraries left out.</summary>
    public void ApplySelection(IReadOnlySet<string> selectedIds)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        void Execute(string sql, string? id = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            if (id is not null)
            {
                command.Parameters.AddWithValue("$id", id);
            }

            command.ExecuteNonQuery();
        }

        Execute("UPDATE libraries SET selected = 0;");
        foreach (var id in selectedIds)
        {
            Execute("UPDATE libraries SET selected = 1 WHERE id = $id;", id);
        }

        Execute("DELETE FROM movies WHERE library_id IN (SELECT id FROM libraries WHERE selected = 0);");
        Execute("DELETE FROM shows WHERE library_id IN (SELECT id FROM libraries WHERE selected = 0);");
        Execute("UPDATE libraries SET last_scanned_at = NULL, last_scan_error = NULL WHERE selected = 0;");
        tx.Commit();
    }

    public void RecordScan(string id, DateTimeOffset? scannedAt, string? error)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE libraries
            SET last_scanned_at = COALESCE($scannedAt, last_scanned_at), last_scan_error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$scannedAt", (object?)scannedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Forgets every library, collection and the whole index, e.g. after switching servers.</summary>
    public void Reset()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collections; DELETE FROM libraries;";
        command.ExecuteNonQuery();
    }
}
