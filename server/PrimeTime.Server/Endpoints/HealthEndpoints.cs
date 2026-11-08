using PrimeTime.Server.Data;

namespace PrimeTime.Server.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// GET /health — unauthenticated liveness/readiness probe.
    /// 200 when the database answers, 503 otherwise. Reveals nothing about Jellyfin.
    /// </summary>
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", (Database database) =>
        {
            var databaseOk = database.IsHealthy();
            var body = new
            {
                status = databaseOk ? "healthy" : "unhealthy",
                checks = new { database = databaseOk ? "ok" : "failed" },
            };
            return databaseOk
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
