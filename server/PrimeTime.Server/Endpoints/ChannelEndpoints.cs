using System.Text.RegularExpressions;
using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;

namespace PrimeTime.Server.Endpoints;

public sealed record ChannelRequest(
    string? Name,
    int? Number,
    string? ContentType,
    string? Mode,
    int? YearFrom,
    int? YearBefore,
    List<string>? Genres,
    List<string>? Studios,
    List<string>? ItemIds,
    string? AirOrder,
    string? Color,
    List<string>? Ratings = null,
    double? MinScore = null,
    int? MinMinutes = null,
    int? MaxMinutes = null,
    int? AddedWithinDays = null,
    List<string>? Keywords = null,
    List<string>? People = null,
    int? SimilarCount = null);

public static partial class ChannelEndpoints
{
    private const int FacetsShown = 60;
    private const int PreviewSamples = 8;
    private const int PeopleShown = 40;
    private const int MaxListEntries = 25;
    private const int MaxEntryLength = 100;
    private const int MaxSeeds = 50;
    private const int MaxLogoBytes = 2 * 1024 * 1024;

    public static void MapChannelEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        // Everything the channel builder offers for movies or for shows.
        api.MapGet("/catalog", (string? contentType, CatalogStore catalog) =>
        {
            var snapshot = catalog.Load();
            var titles = contentType == ChannelContent.Shows ? snapshot.Shows : snapshot.Movies;
            var years = titles.Where(t => t.Year is not null).Select(t => t.Year!.Value).ToList();
            return Results.Ok(new
            {
                items = titles.Select(t => new { t.Id, t.Title, t.Year, t.EpisodeCount }),
                genres = Facet(titles.SelectMany(t => t.Genres)),
                studios = Facet(titles.SelectMany(t => t.Studios)),
                ratings = Facet(titles.Select(t => t.Rating).OfType<string>()),
                keywords = Facet(titles.SelectMany(t => t.Tags)
                    .Where(tag => !ChannelSuggestions.IsUnhelpfulTag(tag, titles.Count))),
                people = titles.SelectMany(t => t.People)
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        name = g.Key,
                        role = g.Any(p => p.Role != "Actor") ? g.First(p => p.Role != "Actor").Role : "Actor",
                        count = g.Count(),
                    })
                    .Where(p => p.count >= 2)
                    .OrderByDescending(p => p.count)
                    .ThenBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                    .Take(PeopleShown),
                minYear = years.Count > 0 ? years.Min() : (int?)null,
                maxYear = years.Count > 0 ? years.Max() : (int?)null,
                collections = snapshot.Collections
                    .Select(c => new
                    {
                        c.Id,
                        c.Name,
                        count = titles.Count(t => snapshot.CollectionMembers.GetValueOrDefault(c.Id)?.Contains(t.Id) == true),
                    })
                    .Where(c => c.count > 0),
            });
        });

        var channels = api.MapGroup("/channels");

        channels.MapGet("/", (ChannelStore store) => Results.Ok(store.List().Select(Describe)));

        channels.MapGet("/suggestions", (ChannelStore store, CatalogStore catalog) =>
            Results.Ok(ChannelSuggestions.Build(catalog.Load(), store.List())));

        channels.MapGet("/{id:long}", (long id, ChannelStore store) =>
            store.Get(id) is { } channel ? Results.Ok(Describe(channel)) : NotFound());

        // What a channel with these rules would play, before it's saved.
        channels.MapPost("/preview", (ChannelRequest body, long? channelId, CatalogStore catalog, ChannelColors colors) =>
        {
            if (ParseDefinition(body, out var error) is not { } definition)
            {
                return Error(error);
            }

            var snapshot = catalog.Load();
            var titles = new ChannelMatcher(definition).Select(snapshot);
            return Results.Ok(new
            {
                automaticColor = colors.Automatic(definition, snapshot, channelId),
                count = titles.Count,
                episodes = titles.Sum(t => t.EpisodeCount),
                hours = (int)(titles.Sum(t => t.RuntimeSeconds) / 3600),
                samples = titles.Take(PreviewSamples).Select(t => t.Title),
            });
        });

        channels.MapPost("/", (ChannelRequest body, ChannelStore store, CatalogStore catalog, ChannelColors colors, ScheduleService schedule) =>
            Save(null, body, store, catalog, colors, schedule));

        channels.MapPut("/{id:long}", (long id, ChannelRequest body, ChannelStore store, CatalogStore catalog, ChannelColors colors, ScheduleService schedule) =>
            store.Get(id) is null ? NotFound() : Save(id, body, store, catalog, colors, schedule));

        channels.MapDelete("/{id:long}", (long id, ChannelStore store) =>
            store.Delete(id) ? Results.NoContent() : NotFound());

        channels.MapGet("/{id:long}/logo", (long id, HttpResponse response, ChannelLogoStore logos) =>
        {
            if (logos.Get(id) is not { } logo)
            {
                return NotFound();
            }

            // The URL changes with the image, so a cached copy never goes stale.
            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(logo.Data, logo.ContentType);
        });

        // The body is the image itself. Its type comes from its contents, not what the client claims.
        channels.MapPut("/{id:long}/logo", async (
            long id, HttpRequest request, ChannelStore store, ChannelLogoStore logos, CancellationToken ct) =>
        {
            if (store.Get(id) is null)
            {
                return NotFound();
            }

            if (await ReadLogoAsync(request, ct) is not { } data)
            {
                return Results.BadRequest(new { error = "Logos can be up to 2 MB." });
            }

            if (DetectImageType(data) is not { } contentType)
            {
                return Results.BadRequest(new { error = "Logos need to be a PNG, JPEG, WebP or GIF image." });
            }

            logos.Save(id, new ChannelLogo(contentType, data));
            return Results.Ok(new { logoUrl = store.Get(id)!.LogoUrl });
        });

        channels.MapDelete("/{id:long}/logo", (long id, ChannelStore store, ChannelLogoStore logos) =>
        {
            if (store.Get(id) is null)
            {
                return NotFound();
            }

            logos.Delete(id);
            return Results.NoContent();
        });
    }

    /// <summary>Returns the request body, or null if it's over the size limit.</summary>
    private static async Task<byte[]?> ReadLogoAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxLogoBytes)
        {
            return null;
        }

        var buffer = new byte[MaxLogoBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length &&
               (read = await request.Body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
        {
            length += read;
        }

        return length > MaxLogoBytes ? null : buffer[..length];
    }

    /// <summary>
    /// Recognises the image formats allowed for logos. SVG is left out on purpose,
    /// because an SVG can carry scripts.
    /// </summary>
    private static string? DetectImageType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        return data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)
            ? "image/webp"
            : null;
    }

    private static IResult Save(
        long? id, ChannelRequest body, ChannelStore store, CatalogStore catalog, ChannelColors colors, ScheduleService schedule)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 60)
        {
            return Error("Give the channel a name (up to 60 characters).");
        }

        if (ParseDefinition(body, out var error) is not { } definition)
        {
            return Error(error);
        }

        var customColor = string.IsNullOrWhiteSpace(body.Color) ? null : body.Color.Trim().ToLowerInvariant();
        if (customColor is not null && !HexColor().IsMatch(customColor))
        {
            return Error("Choose a color like #3d9be9.");
        }

        var snapshot = catalog.Load();
        if (new ChannelMatcher(definition).Select(snapshot).Count == 0)
        {
            return Error("Nothing in your library matches that yet. Widen the rules or choose some titles.");
        }

        var number = body.Number ?? (id is { } existing ? store.Get(existing)!.Number : store.NextNumber());
        if (number is < 1 or > 9999)
        {
            return Error("Channel numbers go from 1 to 9999.");
        }

        if (store.NumberTaken(number, id))
        {
            return Error($"Channel {number} already exists. Choose another number.");
        }

        var previousOrder = id is { } saved ? store.Get(saved)!.Definition.AirOrder : definition.AirOrder;
        var color = customColor ?? colors.Automatic(definition, snapshot, id);
        var channelId = store.Save(id, number, name, definition, color, customColor is not null);
        schedule.RebuildChannel(channelId, airOrderChanged: previousOrder != definition.AirOrder);
        return Results.Ok(Describe(store.Get(channelId)!));
    }

    private static ChannelDefinition? ParseDefinition(ChannelRequest body, out string error)
    {
        error = "";
        if (body.ContentType is not (ChannelContent.Movies or ChannelContent.Shows))
        {
            error = "Choose movies or shows.";
            return null;
        }

        var airOrder = body.AirOrder ?? Guide.AirOrder.DefaultFor(body.ContentType);
        if (!Guide.AirOrder.IsValid(airOrder))
        {
            error = "Choose an air order.";
            return null;
        }

        if (body.Mode is ChannelMode.Pick or ChannelMode.Collection or ChannelMode.Similar)
        {
            var ids = (body.ItemIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
            if (ids.Count == 0)
            {
                error = body.Mode == ChannelMode.Collection ? "Choose at least one collection."
                    : body.Mode == ChannelMode.Similar ? "Choose at least one title to start from."
                    : "Choose at least one title.";
                return null;
            }

            if (body.Mode != ChannelMode.Similar)
            {
                return new ChannelDefinition(body.ContentType, body.Mode, null, null, [], [], ids, airOrder);
            }

            if (ids.Count > MaxSeeds)
            {
                error = $"Start from {MaxSeeds} titles or fewer.";
                return null;
            }

            var similarCount = body.SimilarCount ?? ChannelDefinition.DefaultSimilarCount;
            if (similarCount is < 5 or > 200)
            {
                error = "Add between 5 and 200 similar titles.";
                return null;
            }

            return new ChannelDefinition(body.ContentType, ChannelMode.Similar, null, null, [], [], ids, airOrder)
            {
                SimilarCount = similarCount,
            };
        }

        if (body.Mode != ChannelMode.Filter)
        {
            error = "Choose how to fill the channel.";
            return null;
        }

        if (body.YearFrom is { } from && body.YearBefore is { } before && from >= before)
        {
            error = "The \"before\" year needs to be after the \"from\" year.";
            return null;
        }

        if (body.MinScore is < 0 or > 10)
        {
            error = "Scores go from 0 to 10.";
            return null;
        }

        if (body.MinMinutes is < 1 or > 1000 || body.MaxMinutes is < 1 or > 1000)
        {
            error = "Lengths go from 1 to 1000 minutes.";
            return null;
        }

        if (body.MinMinutes is { } shortest && body.MaxMinutes is { } longest && shortest > longest)
        {
            error = "The longest length needs to be at least the shortest.";
            return null;
        }

        if (body.AddedWithinDays is < 1 or > 3650)
        {
            error = "Choose how recently titles were added, up to 10 years.";
            return null;
        }

        var lists = new[] { body.Genres, body.Studios, body.Ratings, body.Keywords, body.People };
        if (lists.Any(l => l is { Count: > MaxListEntries } || l?.Any(v => v?.Length > MaxEntryLength) == true))
        {
            error = $"Each list can have up to {MaxListEntries} entries of {MaxEntryLength} characters.";
            return null;
        }

        return new ChannelDefinition(
            body.ContentType, ChannelMode.Filter, body.YearFrom, body.YearBefore,
            Clean(body.Genres), Clean(body.Studios), [], airOrder)
        {
            Ratings = Clean(body.Ratings),
            MinScore = body.MinScore,
            MinMinutes = body.MinMinutes,
            MaxMinutes = body.MaxMinutes,
            AddedWithinDays = body.AddedWithinDays,
            Keywords = Clean(body.Keywords).Select(k => k.Trim()).ToList(),
            People = Clean(body.People).Select(p => p.Trim()).ToList(),
        };
    }

    private static List<string> Clean(List<string>? values) =>
        (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static object Facet(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderByDescending(f => f.count)
            .ThenBy(f => f.name, StringComparer.OrdinalIgnoreCase)
            .Take(FacetsShown);

    private static object Describe(Channel channel) => new
    {
        channel.Id,
        channel.Number,
        channel.Name,
        channel.Definition.ContentType,
        channel.Definition.Mode,
        channel.Definition.YearFrom,
        channel.Definition.YearBefore,
        channel.Definition.Genres,
        channel.Definition.Studios,
        channel.Definition.ItemIds,
        channel.Definition.AirOrder,
        channel.Definition.Ratings,
        channel.Definition.MinScore,
        channel.Definition.MinMinutes,
        channel.Definition.MaxMinutes,
        channel.Definition.AddedWithinDays,
        channel.Definition.Keywords,
        channel.Definition.People,
        channel.Definition.SimilarCount,
        channel.LogoUrl,
        channel.Color,
        channel.ColorCustom,
    };

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexColor();

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static IResult NotFound() => Results.NotFound(new { error = "That channel doesn't exist." });
}
