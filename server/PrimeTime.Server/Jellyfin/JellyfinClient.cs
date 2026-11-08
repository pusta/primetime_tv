using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace PrimeTime.Server.Jellyfin;

/// <summary>A Jellyfin call failed in a way we can explain to the admin.</summary>
public sealed class JellyfinException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Thin wrapper over the parts of the Jellyfin HTTP API PrimeTime uses so far.
/// Credentials travel only in the Authorization header, never in URLs, so they
/// don't appear in HttpClient request logs.
/// </summary>
public sealed class JellyfinClient(HttpClient http)
{
    private const string ClientName = "PrimeTime";
    private const string DeviceName = "PrimeTime Server";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Unauthenticated: confirms the URL points at a Jellyfin server.</summary>
    public async Task<JellyfinPublicSystemInfo> GetPublicInfoAsync(string baseUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/System/Info/Public");
        var info = await SendAsync<JellyfinPublicSystemInfo>(request, ct);

        if (info is null || string.IsNullOrEmpty(info.Version) ||
            (info.ProductName is not null && !info.ProductName.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase)))
        {
            throw new JellyfinException("That URL answered, but it doesn't look like a Jellyfin server.");
        }

        return info;
    }

    /// <summary>Exchanges a username and password for an access token.</summary>
    public async Task<JellyfinAuthResult> AuthenticateByNameAsync(
        string baseUrl, string deviceId, string username, string password, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation("Authorization", AuthHeader(deviceId, token: null));
        // Buffered (Content-Length set) rather than streamed, so it also works through
        // reverse proxies that mishandle chunked request bodies.
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { Username = username, Pw = password }),
            System.Text.Encoding.UTF8,
            "application/json");

        var result = await SendAsync<JellyfinAuthResult>(request, ct, unauthorizedMessage:
            "Jellyfin rejected that username or password.");

        if (string.IsNullOrEmpty(result?.AccessToken))
        {
            throw new JellyfinException("Jellyfin accepted the login but returned no access token.");
        }

        return result;
    }

    /// <summary>
    /// Lists libraries. Uses the admin view (with folder paths) when the token allows it,
    /// and falls back to the user's own views for non-admin accounts.
    /// </summary>
    public async Task<IReadOnlyList<LibrarySummary>> GetLibrariesAsync(
        string baseUrl, string deviceId, string token, string? userId, CancellationToken ct)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/Library/VirtualFolders"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", AuthHeader(deviceId, token));
            try
            {
                var folders = await SendAsync<List<JellyfinVirtualFolder>>(request, ct);
                return (folders ?? [])
                    .Select(f => new LibrarySummary(
                        f.ItemId,
                        f.Name ?? "(unnamed)",
                        f.CollectionType ?? "mixed",
                        f.Locations ?? []))
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (JellyfinForbiddenException) when (userId is not null)
            {
                // Not an administrator: fall through to the per-user view.
            }
            catch (JellyfinForbiddenException)
            {
                throw new JellyfinException("That API key isn't allowed to list libraries.");
            }
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get,
                   $"{baseUrl}/UserViews?userId={Uri.EscapeDataString(userId!)}"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", AuthHeader(deviceId, token));
            var views = await SendAsync<JellyfinItemsResult>(request, ct);
            return (views?.Items ?? [])
                .Select(v => new LibrarySummary(v.Id, v.Name ?? "(unnamed)", v.CollectionType ?? "mixed", []))
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Ends a session created by AuthenticateByName (used after a test-only login).</summary>
    public async Task LogoutAsync(string baseUrl, string deviceId, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/Sessions/Logout");
        request.Headers.TryAddWithoutValidation("Authorization", AuthHeader(deviceId, token));
        try
        {
            using var _ = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Best effort; the session expires on its own.
        }
    }

    private static string AuthHeader(string deviceId, string? token)
    {
        var header = $"MediaBrowser Client=\"{ClientName}\", Device=\"{DeviceName}\", " +
                     $"DeviceId=\"{deviceId}\", Version=\"{AppInfo.Version}\"";
        return token is null ? header : header + $", Token=\"{token}\"";
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct,
        string unauthorizedMessage = "Jellyfin rejected the credentials.")
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new JellyfinException("Timed out waiting for the Jellyfin server.", ex);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            throw new JellyfinException($"Couldn't reach the Jellyfin server ({ex.InnerException.Message}).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new JellyfinException($"Couldn't reach the Jellyfin server ({ex.Message}).", ex);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new JellyfinException(unauthorizedMessage);
                case HttpStatusCode.Forbidden:
                    throw new JellyfinForbiddenException();
                case HttpStatusCode.NotFound:
                    throw new JellyfinException(
                        "The server answered 404. Check the URL, including any base path such as /jellyfin.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new JellyfinException($"Jellyfin returned HTTP {(int)response.StatusCode}.");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(Json, ct);
            }
            catch (JsonException ex)
            {
                throw new JellyfinException("That URL answered, but not with Jellyfin's API.", ex);
            }
        }
    }

    private sealed class JellyfinForbiddenException() : Exception("Forbidden");
}
