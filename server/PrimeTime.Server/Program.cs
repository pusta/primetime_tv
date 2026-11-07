using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using PrimeTime.Server;
using PrimeTime.Server.Data;
using PrimeTime.Server.Endpoints;
using PrimeTime.Server.Jellyfin;

// `PrimeTime.Server healthcheck` is used by the Docker HEALTHCHECK so the
// runtime image doesn't need curl or wget.
if (args.Length > 0 && args[0] == "healthcheck")
{
    return await HealthCheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

var options = PrimeTimeOptions.FromConfiguration(builder.Configuration);
Directory.CreateDirectory(options.DataDir);
builder.Services.AddSingleton(options);

// Data Protection encrypts the auth cookie and the saved Jellyfin token.
// Its key ring lives in the data volume so both survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("PrimeTime")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<AdminUserStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<JellyfinConnectionStore>();
builder.Services.AddHttpClient<JellyfinClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<JellyfinService>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "primetime.auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromDays(7);
        cookie.SlidingExpiration = true;
        // This is a JSON API: answer 401/403 instead of redirecting to a login page.
        cookie.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

// Slow down password guessing on the login and first-run setup endpoints.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

app.Services.GetRequiredService<Database>().Migrate();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapJellyfinEndpoints();

app.Logger.LogInformation("PrimeTime {Version} starting, data directory {DataDir}",
    AppInfo.Version, options.DataDir);

app.Run();
return 0;

namespace PrimeTime.Server
{
    public static class AppInfo
    {
        public static string Version { get; } =
            typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+')[0] ?? "0.0.0";
    }
}
