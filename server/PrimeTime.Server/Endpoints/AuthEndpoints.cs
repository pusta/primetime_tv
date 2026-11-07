using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Endpoints;

public sealed record CredentialsRequest(string? Username, string? Password);

public static class AuthEndpoints
{
    private const int MinPasswordLength = 8;

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Tells the UI which screen to show: first-run setup, login, or the dashboard.
        api.MapGet("/session", (HttpContext http, AdminUserStore users) => Results.Ok(new
        {
            needsSetup = !users.AnyExist(),
            authenticated = http.User.Identity?.IsAuthenticated == true,
            username = http.User.Identity?.Name,
        }));

        // Creates the first admin account. Only works while no admin exists.
        api.MapPost("/setup", async (CredentialsRequest body, HttpContext http, AdminUserStore users) =>
        {
            var username = body.Username?.Trim() ?? "";
            var password = body.Password ?? "";
            if (username.Length is < 1 or > 64)
            {
                return Error("Choose a username (up to 64 characters).");
            }

            if (password.Length < MinPasswordLength)
            {
                return Error($"Use a password of at least {MinPasswordLength} characters.");
            }

            var admin = users.CreateFirstAdmin(username, password);
            if (admin is null)
            {
                return Results.Json(new { error = "Setup is already complete. Sign in instead." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            await SignInAsync(http, admin);
            return Results.Ok(new { username = admin.Username });
        }).RequireRateLimiting("auth");

        api.MapPost("/login", async (CredentialsRequest body, HttpContext http, AdminUserStore users) =>
        {
            var admin = users.VerifyCredentials(body.Username?.Trim() ?? "", body.Password ?? "");
            if (admin is null)
            {
                return Results.Json(new { error = "Wrong username or password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            await SignInAsync(http, admin);
            return Results.Ok(new { username = admin.Username });
        }).RequireRateLimiting("auth");

        api.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
    }

    private static Task SignInAsync(HttpContext http, AdminUser admin)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.Username),
                new Claim(ClaimTypes.Role, "admin"),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    private static IResult Error(string message) => Results.BadRequest(new { error = message });
}
