using PrimeTime.Server.Data;
using PrimeTime.Server.Jellyfin;

namespace PrimeTime.Server.Endpoints;

public static class JellyfinEndpoints
{
    public static void MapJellyfinEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/jellyfin").RequireAuthorization();

        // The saved connection settings, without any secret.
        api.MapGet("/", (JellyfinConnectionStore store) =>
        {
            var saved = store.Get();
            return saved is null
                ? Results.Ok(new { configured = false })
                : Results.Ok(new
                {
                    configured = true,
                    url = saved.BaseUrl,
                    authMethod = saved.AuthMethod,
                    username = saved.Username,
                    serverName = saved.ServerName,
                    serverVersion = saved.ServerVersion,
                    updatedAt = saved.UpdatedAt,
                });
        });

        // Live check of the saved connection: server info and libraries.
        api.MapGet("/status", async (JellyfinService jellyfin, CancellationToken ct) =>
        {
            try
            {
                var result = await jellyfin.CheckSavedAsync(ct);
                return result is null
                    ? Results.Ok(new { configured = false, connected = false })
                    : Results.Ok(new { configured = true, connected = true, result.Server, result.Libraries });
            }
            catch (JellyfinException ex)
            {
                return Results.Ok(new { configured = true, connected = false, error = ex.Message });
            }
        });

        // Try the submitted details without saving them.
        api.MapPost("/test", (JellyfinConnectRequest body, JellyfinService jellyfin, CancellationToken ct) =>
            Run(() => jellyfin.TestAsync(body, ct)));

        // Try the submitted details and save them if they work.
        api.MapPut("/", (JellyfinConnectRequest body, JellyfinService jellyfin, CancellationToken ct) =>
            Run(() => jellyfin.SaveAsync(body, ct)));

        api.MapDelete("/", async (JellyfinService jellyfin, CancellationToken ct) =>
        {
            await jellyfin.DisconnectAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> Run(Func<Task<JellyfinConnectResult>> action)
    {
        try
        {
            var result = await action();
            return Results.Ok(new { result.Server, result.Libraries });
        }
        catch (JellyfinException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
