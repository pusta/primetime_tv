namespace PrimeTime.Server.Data;

public sealed record ChannelLogo(string ContentType, byte[] Data);

/// <summary>Images the admin uploaded for channels. They go when their channel does.</summary>
public sealed class ChannelLogoStore(Database database)
{
    public ChannelLogo? Get(long channelId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_type, data FROM channel_logos WHERE channel_id = $id;";
        command.Parameters.AddWithValue("$id", channelId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ChannelLogo(reader.GetString(0), (byte[])reader[1]) : null;
    }

    public void Save(long channelId, ChannelLogo logo)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_logos (channel_id, content_type, data, updated_at)
            VALUES ($id, $type, $data, $now)
            ON CONFLICT (channel_id) DO UPDATE SET
                content_type = excluded.content_type, data = excluded.data, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", channelId);
        command.Parameters.AddWithValue("$type", logo.ContentType);
        command.Parameters.AddWithValue("$data", logo.Data);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    public void Delete(long channelId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channel_logos WHERE channel_id = $id;";
        command.Parameters.AddWithValue("$id", channelId);
        command.ExecuteNonQuery();
    }
}
