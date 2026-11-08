using System.Security.Cryptography;
using System.Text;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Guide;

/// <summary>Picks a guide color that suits what a channel plays and stands apart from the others.</summary>
public sealed class ChannelColors(ChannelStore channels, CatalogStore catalog)
{
    // In order around the color wheel, so neighboring entries look related.
    private static readonly string[] Palette =
    [
        "#e5484d", "#f2643d", "#f5892a", "#f0a92e", "#e9c53a", "#a3c93a",
        "#4caf50", "#2fae7f", "#1fb0a6", "#22b1d1", "#3d9be9", "#4a7dea",
        "#6a67e8", "#8e5be0", "#b14fd6", "#d64bb3", "#ea4f8f", "#e8476a",
    ];

    // Checked in order against the genre name, so "Sci-Fi & Fantasy" counts as sci-fi.
    private static readonly (string Keyword, int Slot)[] GenreSlots =
    [
        ("horror", 0), ("thriller", 17), ("action", 1), ("western", 2), ("adventure", 3), ("game", 3),
        ("comedy", 4), ("war", 5), ("kids", 5), ("children", 5), ("animation", 5), ("anime", 5),
        ("sport", 6), ("family", 7), ("documentary", 8), ("history", 8), ("biography", 8),
        ("sci-fi", 9), ("science fiction", 9), ("talk", 10), ("news", 11), ("crime", 11),
        ("mystery", 12), ("fantasy", 13), ("drama", 14), ("music", 15), ("reality", 15),
        ("romance", 16), ("soap", 16),
    ];

    /// <summary>
    /// The nearest color to the channel's content that no other channel is using.
    /// Pass the channel's own ID when it already exists, so it doesn't count against itself.
    /// </summary>
    public string Automatic(ChannelDefinition definition, CatalogSnapshot snapshot, long? channelId)
    {
        var taken = channels.List()
            .Where(c => c.Id != channelId && c.Color is not null)
            .Select(c => c.Color!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var start = StartingSlot(definition, snapshot);
        for (var step = 0; step < Palette.Length; step++)
        {
            // Spread outward from the best match: 0, +1, -1, +2, -2...
            var offset = step % 2 == 0 ? step / 2 : -(step + 1) / 2;
            var candidate = Palette[((start + offset) % Palette.Length + Palette.Length) % Palette.Length];
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return Palette[start];
    }

    /// <summary>Gives a color to channels that don't have one yet, such as those made before colors existed.</summary>
    public void FillMissing()
    {
        var missing = channels.List().Where(c => c.Color is null).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var snapshot = catalog.Load();
        foreach (var channel in missing)
        {
            channels.SetColor(channel.Id, Automatic(channel.Definition, snapshot, channel.Id));
        }
    }

    private static int StartingSlot(ChannelDefinition definition, CatalogSnapshot snapshot)
    {
        var genres = definition.Genres.Count > 0
            ? definition.Genres
            : new ChannelMatcher(definition).Select(snapshot)
                .SelectMany(t => t.Genres)
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();

        foreach (var genre in genres)
        {
            foreach (var (keyword, slot) in GenreSlots)
            {
                if (genre.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return slot;
                }
            }
        }

        // No recognizable genre: still pick the same color every time for the same content.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(definition.Signature));
        return hash[0] % Palette.Length;
    }
}
