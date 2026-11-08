using System.Text.Json;
using PrimeTime.Server.Guide;

namespace PrimeTime.Server.Data;

public sealed class ChannelStore(Database database)
{
    public bool Any()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM channels);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<Channel> List()
    {
        using var connection = database.Open();

        var picks = new Dictionary<long, List<string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT channel_id, item_id FROM channel_items;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (!picks.TryGetValue(id, out var list))
                {
                    picks[id] = list = [];
                }

                list.Add(reader.GetString(1));
            }
        }

        var result = new List<Channel>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, number, name, content_type, mode, year_from, year_before, genres, studios, air_order,
                       (SELECT updated_at FROM channel_logos l WHERE l.channel_id = channels.id),
                       color, color_custom
                FROM channels ORDER BY number;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                result.Add(new Channel(id, reader.GetInt32(1), reader.GetString(2), new ChannelDefinition(
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
                    JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? [],
                    picks.GetValueOrDefault(id) ?? [],
                    reader.GetString(9)),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt64(12) == 1));
            }
        }

        return result;
    }

    public Channel? Get(long id) => List().FirstOrDefault(c => c.Id == id);

    public int NextNumber()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(number), 0) + 1 FROM channels;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public bool NumberTaken(int number, long? exceptId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM channels WHERE number = $number AND id <> $id);";
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$id", exceptId ?? 0);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public long Save(long? id, int number, string name, ChannelDefinition definition, string color, bool colorCustom)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        long channelId;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = id is null
                ? """
                  INSERT INTO channels
                      (number, name, content_type, mode, year_from, year_before, genres, studios, air_order,
                       color, color_custom, created_at, updated_at)
                  VALUES ($number, $name, $type, $mode, $from, $before, $genres, $studios, $airOrder,
                          $color, $colorCustom, $now, $now)
                  RETURNING id;
                  """
                : """
                  UPDATE channels SET number = $number, name = $name, content_type = $type, mode = $mode,
                      year_from = $from, year_before = $before, genres = $genres, studios = $studios,
                      air_order = $airOrder, color = $color, color_custom = $colorCustom, updated_at = $now
                  WHERE id = $id
                  RETURNING id;
                  """;
            command.Parameters.AddWithValue("$id", id ?? 0);
            command.Parameters.AddWithValue("$number", number);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$type", definition.ContentType);
            command.Parameters.AddWithValue("$mode", definition.Mode);
            command.Parameters.AddWithValue("$from", (object?)definition.YearFrom ?? DBNull.Value);
            command.Parameters.AddWithValue("$before", (object?)definition.YearBefore ?? DBNull.Value);
            command.Parameters.AddWithValue("$genres", JsonSerializer.Serialize(definition.Genres));
            command.Parameters.AddWithValue("$studios", JsonSerializer.Serialize(definition.Studios));
            command.Parameters.AddWithValue("$airOrder", definition.AirOrder);
            command.Parameters.AddWithValue("$color", color);
            command.Parameters.AddWithValue("$colorCustom", colorCustom ? 1 : 0);
            command.Parameters.AddWithValue("$now", now);
            channelId = Convert.ToInt64(command.ExecuteScalar());
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM channel_items WHERE channel_id = $id;";
            clear.Parameters.AddWithValue("$id", channelId);
            clear.ExecuteNonQuery();
        }

        foreach (var itemId in definition.ItemIds.Distinct())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO channel_items (channel_id, item_id) VALUES ($id, $item);";
            insert.Parameters.AddWithValue("$id", channelId);
            insert.Parameters.AddWithValue("$item", itemId);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return channelId;
    }

    public void SetColor(long id, string color)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE channels SET color = $color WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$color", color);
        command.ExecuteNonQuery();
    }

    public bool Delete(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channels WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public void DeleteAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channels;";
        command.ExecuteNonQuery();
    }
}
