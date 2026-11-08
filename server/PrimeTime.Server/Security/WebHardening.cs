using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace PrimeTime.Server.Security;

/// <summary>Reverse-proxy trust and the security headers sent with every response.</summary>
public static class WebHardening
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; " +
        "connect-src 'self'; " +
        "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    /// <summary>Must come before anything that reads the client's address or the request scheme.</summary>
    public static void UseWebHardening(this WebApplication app, string? trustedProxies)
    {
        if (ForwardedHeaders(trustedProxies) is { } forwarded)
        {
            app.UseForwardedHeaders(forwarded);
        }

        var warned = 0;
        app.Use((context, next) =>
        {
            if (FromUntrustedProxy(context.Request) && Interlocked.Exchange(ref warned, 1) == 0)
            {
                app.Logger.LogWarning(
                    "Received X-Forwarded headers from an address PrimeTime doesn't trust, so they were ignored. " +
                    "If PrimeTime is behind a reverse proxy, set PRIMETIME_TRUSTED_PROXIES to its address.");
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            return next();
        });
    }

    /// <summary>
    /// Only addresses the admin lists are believed; otherwise anyone could fake their IP or HTTPS.
    /// Null when none are listed, because the middleware trusts everyone if both of its lists are empty.
    /// </summary>
    private static ForwardedHeadersOptions? ForwardedHeaders(string? trustedProxies)
    {
        var entries = (trustedProxies ?? "").Split([',', ';', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var entry in entries)
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"PRIMETIME_TRUSTED_PROXIES has an entry that isn't an IP address or CIDR range: {entry}");
            }
        }

        return options;
    }

    // Headers from a trusted proxy are consumed and leave an X-Original-* copy behind.
    private static bool FromUntrustedProxy(HttpRequest request) =>
        (request.Headers.ContainsKey("X-Forwarded-For") || request.Headers.ContainsKey("X-Forwarded-Proto")) &&
        !request.Headers.ContainsKey("X-Original-For") &&
        !request.Headers.ContainsKey("X-Original-Proto");
}
