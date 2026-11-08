using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;
using PrimeTime.Server.Library;
using PrimeTime.Server.Tasks;

namespace PrimeTime.Server.Jellyfin;

/// <summary>Connection details submitted from the management UI.</summary>
public sealed record JellyfinConnectRequest(
    string? Url,
    string? AuthMethod,
    string? ApiKey,
    string? Username,
    string? Password);

public sealed record JellyfinConnectResult(ServerSummary Server, IReadOnlyList<LibrarySummary> Libraries);

/// <summary>Tests, saves and checks the Jellyfin connection.</summary>
public sealed class JellyfinService(
    JellyfinClient client,
    JellyfinConnectionStore store,
    SettingsStore settings,
    LibraryStore libraries,
    ScheduledTaskStore scheduledTasks,
    ScheduleService schedule,
    TaskRunner runner,
    ILogger<JellyfinService> logger)
{
    private const string ChannelsServerKey = "channels_server_id";

    /// <summary>Connects with the given details and reports what it found, without saving anything.</summary>
    public async Task<JellyfinConnectResult> TestAsync(JellyfinConnectRequest request, CancellationToken ct)
    {
        var attempt = await ConnectAsync(request, ct);
        if (attempt.CreatedSession)
        {
            await client.LogoutAsync(attempt.BaseUrl, settings.GetOrCreateDeviceId(), attempt.Token, ct);
        }

        return attempt.Result;
    }

    /// <summary>Connects with the given details and, if that works, saves them.</summary>
    public async Task<JellyfinConnectResult> SaveAsync(JellyfinConnectRequest request, CancellationToken ct)
    {
        var attempt = await ConnectAsync(request, ct);
        var connection = new SavedJellyfinConnection(
            BaseUrl: attempt.BaseUrl,
            AuthMethod: attempt.AuthMethod,
            Username: attempt.Username,
            UserId: attempt.UserId,
            Token: attempt.Token,
            ServerId: attempt.Result.Server.Id,
            ServerName: attempt.Result.Server.Name,
            ServerVersion: attempt.Result.Server.Version,
            UpdatedAt: DateTimeOffset.UtcNow);

        var previous = store.Get();
        if (previous is not null && previous.ServerId != connection.ServerId)
        {
            // A different server has different items, so the old selection and index mean nothing.
            await runner.RunExclusiveAsync(() =>
            {
                store.Save(connection);
                ForgetLibraries();
            });
            logger.LogInformation("Jellyfin server changed; cleared the library selection and index");
        }
        else
        {
            store.Save(connection);
        }

        ForgetChannelsIfServerChanged(connection.ServerId);

        logger.LogInformation("Saved Jellyfin connection to {ServerName} ({Version}) using {AuthMethod}",
            attempt.Result.Server.Name, attempt.Result.Server.Version, attempt.AuthMethod);
        return attempt.Result;
    }

    /// <summary>Live check of the saved connection. Returns null when nothing is saved.</summary>
    public async Task<JellyfinConnectResult?> CheckSavedAsync(CancellationToken ct)
    {
        var saved = store.Get();
        if (saved is null)
        {
            return null;
        }

        var info = await client.GetPublicInfoAsync(saved.BaseUrl, ct);
        var libraries = await client.GetLibrariesAsync(
            saved.BaseUrl, settings.GetOrCreateDeviceId(), saved.Token, saved.UserId, ct);
        return new JellyfinConnectResult(
            new ServerSummary(saved.BaseUrl, info.ServerName, info.Version, info.Id), libraries);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var saved = store.Get();
        if (saved is null)
        {
            return;
        }

        if (saved.AuthMethod == JellyfinAuthMethod.Password)
        {
            await client.LogoutAsync(saved.BaseUrl, settings.GetOrCreateDeviceId(), saved.Token, ct);
        }

        await runner.RunExclusiveAsync(() =>
        {
            store.Delete();
            ForgetLibraries();
        });
        logger.LogInformation("Removed the saved Jellyfin connection");
    }

    private void ForgetLibraries()
    {
        libraries.Reset();
        scheduledTasks.SetNextRun(LibraryScanTask.TaskKey, null);
        schedule.RefreshAll();
    }

    // Channels survive a disconnect and reconnect to the same server, but a different
    // server has different items, so remember which server they were built for.
    private void ForgetChannelsIfServerChanged(string? serverId)
    {
        var channelsServer = settings.Get(ChannelsServerKey);
        if (channelsServer is not null && channelsServer != serverId)
        {
            schedule.DeleteAllChannels();
            logger.LogInformation("Jellyfin server changed; removed channels built for the old server");
        }

        if (serverId is not null)
        {
            settings.Set(ChannelsServerKey, serverId);
        }
    }

    private async Task<ConnectAttempt> ConnectAsync(JellyfinConnectRequest request, CancellationToken ct)
    {
        var baseUrl = NormalizeUrl(request.Url);
        var deviceId = settings.GetOrCreateDeviceId();
        var info = await client.GetPublicInfoAsync(baseUrl, ct);

        string token, authMethod;
        string? userId = null, username = null;
        var createdSession = false;

        switch (request.AuthMethod)
        {
            case JellyfinAuthMethod.ApiKey:
                authMethod = JellyfinAuthMethod.ApiKey;
                token = request.ApiKey?.Trim() ?? "";
                if (token.Length == 0)
                {
                    // Blank means "keep the key that's already saved" for the same server.
                    var saved = store.Get();
                    if (saved is { AuthMethod: JellyfinAuthMethod.ApiKey } && saved.BaseUrl == baseUrl)
                    {
                        token = saved.Token;
                    }
                    else
                    {
                        throw new JellyfinException("Enter an API key.");
                    }
                }
                break;

            case JellyfinAuthMethod.Password:
                authMethod = JellyfinAuthMethod.Password;
                username = request.Username?.Trim();
                if (string.IsNullOrEmpty(username))
                {
                    throw new JellyfinException("Enter a Jellyfin username.");
                }

                var auth = await client.AuthenticateByNameAsync(baseUrl, deviceId, username, request.Password ?? "", ct);
                token = auth.AccessToken!;
                userId = auth.User?.Id;
                username = auth.User?.Name ?? username;
                createdSession = true;
                break;

            default:
                throw new JellyfinException("Choose API key or username and password.");
        }

        var libraries = await client.GetLibrariesAsync(baseUrl, deviceId, token, userId, ct);
        var result = new JellyfinConnectResult(new ServerSummary(baseUrl, info.ServerName, info.Version, info.Id), libraries);
        return new ConnectAttempt(baseUrl, authMethod, username, userId, token, createdSession, result);
    }

    /// <summary>Accepts "http(s)://host[:port][/base]" and strips trailing slashes, query and fragment.</summary>
    public static string NormalizeUrl(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new JellyfinException("Enter the Jellyfin server URL.");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new JellyfinException("The server URL must look like http://host:8096.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new JellyfinException("Don't put credentials in the URL; use the fields below.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private sealed record ConnectAttempt(
        string BaseUrl,
        string AuthMethod,
        string? Username,
        string? UserId,
        string Token,
        bool CreatedSession,
        JellyfinConnectResult Result);
}
