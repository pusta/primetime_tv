namespace PrimeTime.Server.Data;

public sealed record StoredCollection(string Id, string Name);

/// <summary>Jellyfin collections and which items each one holds.</summary>
public sealed class CollectionStore(Database database)
{
    public IReadOnlyList<StoredCollection> List()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name FROM collections ORDER BY COALESCE(sort_name, name) COLLATE NOCASE;";

        var result = new List<StoredCollection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StoredCollection(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public Dictionary<string, HashSet<string>> Members()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT collection_id, item_id FROM collection_items;";

        var result = new Dictionary<string, HashSet<string>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var items))
            {
                result[id] = items = [];
            }

            items.Add(reader.GetString(1));
        }

        return result;
    }

    /// <summary>Saves a collection's name and contents.</summary>
    public void Replace(string id, string name, string? sortName, IReadOnlySet<string> itemIds)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO collections (id, name, sort_name) VALUES ($id, $name, $sortName)
                ON CONFLICT (id) DO UPDATE SET name = excluded.name, sort_name = excluded.sort_name;
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$name", name);
            upsert.Parameters.AddWithValue("$sortName", (object?)sortName ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM collection_items WHERE collection_id = $id;";
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }

        foreach (var itemId in itemIds)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO collection_items (collection_id, item_id) VALUES ($id, $item);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$item", itemId);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void DeleteAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM collections;";
        command.ExecuteNonQuery();
    }
}
