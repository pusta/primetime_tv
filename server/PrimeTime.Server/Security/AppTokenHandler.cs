using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PrimeTime.Server.Data;

namespace PrimeTime.Server.Security;

/// <summary>Signs apps in with the bearer token they got from device linking.</summary>
public sealed class AppTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeviceStore devices) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AppToken";
    public const string PolicyName = "App";
    public const string DeviceClaim = "primetime:device";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (devices.FindByToken(header["Bearer ".Length..].Trim()) is not { } device)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown token."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, device.Name),
                new Claim(DeviceClaim, device.Id.ToString()),
            ],
            SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }
}
