using System.Security.Claims;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Endpoints;

public sealed record DeviceCodeRequest(string? Code);

/// <summary>The website's side of device linking: approving a code and managing linked devices.</summary>
public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/devices").RequireAuthorization();

        api.MapGet("/", (DeviceStore devices) => Results.Ok(devices.List().Select(d => new
        {
            d.Id, d.Name, d.Platform, d.CreatedAt, d.LastSeenAt,
        })));

        api.MapDelete("/{id:long}", (long id, DeviceStore devices) =>
            devices.Revoke(id) ? Results.NoContent() : Results.NotFound(new { error = "That device isn't linked." }));

        // Shows what's asking to link, so the admin can check before approving.
        api.MapGet("/link", (string? code, DeviceStore devices) =>
            devices.FindPending(code ?? "") is { } pending
                ? Results.Ok(new { pending.Name, pending.Platform, pending.ExpiresAt })
                : LinkNotFound()).RequireRateLimiting("auth");

        api.MapPost("/link/approve", (DeviceCodeRequest body, HttpContext http, DeviceStore devices) =>
        {
            var adminId = long.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return devices.Approve(body.Code ?? "", adminId) ? Results.NoContent() : LinkNotFound();
        }).RequireRateLimiting("auth");

        api.MapPost("/link/deny", (DeviceCodeRequest body, DeviceStore devices) =>
            devices.Deny(body.Code ?? "") ? Results.NoContent() : LinkNotFound()).RequireRateLimiting("auth");
    }

    private static IResult LinkNotFound() =>
        Results.NotFound(new { error = "That code isn't valid or has expired. Start again on your device." });
}
