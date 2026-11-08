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
    string? AirOrder);

public static class ChannelEndpoints
{
    private const int FacetsShown = 60;
    private const int PreviewSamples = 8;

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
        channels.MapPost("/preview", (ChannelRequest body, CatalogStore catalog) =>
        {
            if (ParseDefinition(body, out var error) is not { } definition)
            {
                return Error(error);
            }

            var titles = new ChannelMatcher(definition).Select(catalog.Load());
            return Results.Ok(new
            {
                count = titles.Count,
                episodes = titles.Sum(t => t.EpisodeCount),
                hours = (int)(titles.Sum(t => t.RuntimeSeconds) / 3600),
                samples = titles.Take(PreviewSamples).Select(t => t.Title),
            });
        });

        channels.MapPost("/", (ChannelRequest body, ChannelStore store, CatalogStore catalog, ScheduleService schedule) =>
            Save(null, body, store, catalog, schedule));

        channels.MapPut("/{id:long}", (long id, ChannelRequest body, ChannelStore store, CatalogStore catalog, ScheduleService schedule) =>
            store.Get(id) is null ? NotFound() : Save(id, body, store, catalog, schedule));

        channels.MapDelete("/{id:long}", (long id, ChannelStore store) =>
            store.Delete(id) ? Results.NoContent() : NotFound());
    }

    private static IResult Save(
        long? id, ChannelRequest body, ChannelStore store, CatalogStore catalog, ScheduleService schedule)
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

        if (new ChannelMatcher(definition).Select(catalog.Load()).Count == 0)
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
        var channelId = store.Save(id, number, name, definition);
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

        if (body.Mode is ChannelMode.Pick or ChannelMode.Collection)
        {
            var ids = (body.ItemIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
            if (ids.Count == 0)
            {
                error = body.Mode == ChannelMode.Pick ? "Choose at least one title." : "Choose at least one collection.";
                return null;
            }

            return new ChannelDefinition(body.ContentType, body.Mode, null, null, [], [], ids, airOrder);
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

        return new ChannelDefinition(
            body.ContentType, ChannelMode.Filter, body.YearFrom, body.YearBefore,
            Clean(body.Genres), Clean(body.Studios), [], airOrder);
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
    };

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static IResult NotFound() => Results.NotFound(new { error = "That channel doesn't exist." });
}
