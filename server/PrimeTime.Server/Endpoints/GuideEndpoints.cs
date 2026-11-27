using PrimeTime.Server.Data;

namespace PrimeTime.Server.Endpoints;

public static class GuideEndpoints
{
    private const long MaxWindowSeconds = 48 * 3600;

    public static void MapGuideEndpoints(this WebApplication app)
    {
        // Every channel with its programs in a time window (Unix seconds).
        app.MapGet("/api/guide", (long from, long to, ChannelStore channels, ScheduleStore schedule, CatalogStore catalog) =>
        {
            if (to <= from || to - from > MaxWindowSeconds)
            {
                return Results.BadRequest(new { error = "Ask for a window of up to 48 hours." });
            }

            var entries = schedule.Range(from, to);
            var byChannel = entries.ToLookup(e => e.ChannelId);
            var facts = catalog.Facts(
                entries.Where(e => e.ShowId is null).Select(e => e.ItemId).ToHashSet(),
                entries.Where(e => e.ShowId is not null).Select(e => e.ShowId!).ToHashSet());
            TitleFacts? FactsFor(ScheduleEntry e) => facts.GetValueOrDefault(e.ShowId ?? e.ItemId);
            return Results.Ok(new
            {
                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                channels = channels.List().Select(channel => new
                {
                    channel.Id,
                    channel.Number,
                    channel.Name,
                    channel.LogoUrl,
                    channel.Color,
                    programs = byChannel[channel.Id].Select(e => new
                    {
                        type = e.ItemType,
                        itemId = e.ItemId,
                        title = e.Title,
                        subtitle = e.Subtitle,
                        season = e.Season,
                        episode = e.Episode,
                        genres = FactsFor(e)?.Genres.Take(2),
                        year = FactsFor(e)?.Year,
                        rating = FactsFor(e)?.Rating,
                        start = e.StartTs,
                        end = e.EndTs,
                    }),
                }),
            });
        }).RequireAuthorization();
    }
}
