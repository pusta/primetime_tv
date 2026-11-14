using System.Security.Claims;
using PrimeTime.Server.Data;
using PrimeTime.Server.Guide;
using PrimeTime.Server.Jellyfin;
using PrimeTime.Server.Playback;
using PrimeTime.Server.Security;

namespace PrimeTime.Server.Endpoints;

public sealed record DeviceStartRequest(string? DeviceName, string? Platform);

public sealed record DeviceTokenRequest(string? DeviceCode);

public sealed record AppPlayRequest(string? Player, int? AudioStreamIndex);

/// <summary>
/// The stable API for the native apps; see docs/app-api.md. App tokens can watch but never
/// administer, so everything an app can do is here and the admin routes keep requiring a web sign-in.
/// </summary>
public static class AppApiEndpoints
{
    public const string Prefix = "/api/app/v1";

    private const int MaxGuideWindowSeconds = 48 * 3600;
    private const int MaxDeviceNameLength = 64;
    private const int MaxPlatformLength = 32;

    public static void MapAppApiEndpoints(this WebApplication app)
    {
        var open = app.MapGroup(Prefix);
        var signedIn = app.MapGroup(Prefix).RequireAuthorization(AppTokenHandler.PolicyName);

        open.MapGet("/server", (AdminUserStore users, SettingsStore settings, PrimeTimeOptions options) => Results.Ok(new
        {
            name = "PrimeTime",
            apiVersion = 1,
            ready = users.AnyExist() && settings.IsSetupComplete(),
            publicUrl = options.PublicUrl,
        }));

        open.MapPost("/device/start", (
            DeviceStartRequest body, HttpRequest request, DeviceStore devices, AdminUserStore users,
            SettingsStore settings, PrimeTimeOptions options) =>
        {
            // The approval page only opens once setup is finished.
            if (!users.AnyExist() || !settings.IsSetupComplete())
            {
                return Results.Conflict(new { error = "server_not_set_up" });
            }

            var name = body.DeviceName?.Trim() ?? "";
            if (name.Length is < 1 or > MaxDeviceNameLength)
            {
                return Results.BadRequest(new { error = "invalid_device_name" });
            }

            var platform = body.Platform?.Trim();
            if (platform is { Length: > MaxPlatformLength })
            {
                return Results.BadRequest(new { error = "invalid_platform" });
            }

            if (devices.StartLink(name, string.IsNullOrEmpty(platform) ? null : platform) is not { } link)
            {
                return Results.Json(new { error = "too_many_requests" }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            var site = options.PublicUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}";
            var linkPage = $"{site}/#/link";
            return Results.Ok(new
            {
                deviceCode = link.DeviceCode,
                userCode = link.UserCode,
                verificationUri = linkPage,
                verificationUriComplete = $"{linkPage}?code={link.UserCode}",
                expiresIn = link.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                interval = DeviceStore.PollIntervalSeconds,
            });
        }).RequireRateLimiting("auth");

        open.MapPost("/device/token", (DeviceTokenRequest body, DeviceStore devices) =>
        {
            var poll = devices.Poll(body.DeviceCode ?? "");
            return poll.Status switch
            {
                DevicePollStatus.Approved => Results.Ok(new { accessToken = poll.AccessToken, tokenType = "Bearer" }),
                DevicePollStatus.Pending => TokenError("authorization_pending"),
                DevicePollStatus.SlowDown => TokenError("slow_down"),
                DevicePollStatus.Denied => TokenError("access_denied"),
                DevicePollStatus.Expired => TokenError("expired_token"),
                _ => TokenError("invalid_grant"),
            };
        }).RequireRateLimiting("device-poll");

        signedIn.MapGet("/me", (HttpContext http) => Results.Ok(new
        {
            deviceId = long.Parse(http.User.FindFirstValue(AppTokenHandler.DeviceClaim)!),
            deviceName = http.User.Identity!.Name,
        }));

        signedIn.MapPost("/signout", (HttpContext http, DeviceStore devices) =>
        {
            devices.Revoke(long.Parse(http.User.FindFirstValue(AppTokenHandler.DeviceClaim)!));
            return Results.NoContent();
        });

        signedIn.MapGet("/channels", (ChannelStore channels) =>
            Results.Ok(channels.List().Select(DescribeChannel)));

        signedIn.MapGet("/channels/{id:long}/logo", (long id, HttpResponse response, ChannelLogoStore logos) =>
        {
            if (logos.Get(id) is not { } logo)
            {
                return Results.NotFound(new { error = "not_found" });
            }

            response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return Results.File(logo.Data, logo.ContentType);
        });

        signedIn.MapGet("/guide", (long from, long to, ChannelStore channels, ScheduleStore schedule) =>
        {
            if (to <= from || to - from > MaxGuideWindowSeconds)
            {
                return Results.BadRequest(new { error = "invalid_window" });
            }

            var byChannel = schedule.Range(from, to).ToLookup(e => e.ChannelId);
            return Results.Ok(new
            {
                now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                channels = channels.List().Select(channel => new
                {
                    channel = DescribeChannel(channel),
                    programs = byChannel[channel.Id].Select(e => new
                    {
                        type = e.ItemType,
                        title = e.Title,
                        subtitle = e.Subtitle,
                        start = e.StartTs,
                        end = e.EndTs,
                    }),
                }),
            });
        });

        signedIn.MapPost("/channels/{id:long}/play", async (
            long id, AppPlayRequest? body, PlaybackService playback, CancellationToken ct) =>
        {
            AppPlayer? player = body?.Player switch
            {
                null or "native" => AppPlayer.Native,
                "mpv" => AppPlayer.Mpv,
                "auto" => AppPlayer.Auto,
                _ => null,
            };
            if (player is null || body?.AudioStreamIndex < 0)
            {
                return Results.BadRequest(new { error = player is null ? "invalid_player" : "invalid_audio" });
            }

            try
            {
                var (channel, start) = await playback.StartAsync(id, player.Value, body?.AudioStreamIndex, ct);
                if (channel is null)
                {
                    return Results.NotFound(new { error = "not_found" });
                }

                if (start is null)
                {
                    return Results.Conflict(new { error = "nothing_on_air" });
                }

                var stream = $"{Prefix}/play/{start.Session.Id}";
                return Results.Ok(new
                {
                    playId = start.Session.Id,
                    hlsUrl = $"{stream}/master.m3u8",
                    directUrl = start.Player == AppPlayer.Mpv ? $"{stream}/direct" : null,
                    player = start.Player == AppPlayer.Native ? "native" : "mpv",
                    video = start.Source.Video is { } video
                        ? new
                        {
                            codec = video.Codec,
                            range = video.VideoRangeType ?? video.VideoRange,
                            width = video.Width,
                            height = video.Height,
                        }
                        : null,
                    audioStreamIndex = start.AudioStreamIndex,
                    audioTracks = start.Source.AudioStreams.Select(a => new
                    {
                        index = a.Index,
                        language = a.Language,
                        title = a.Title,
                        displayTitle = a.DisplayTitle,
                        codec = a.Codec,
                        channels = a.Channels,
                        isDefault = a.IsDefault,
                    }),
                    now = start.Now,
                    offsetSeconds = start.Now - start.Program.StartTs,
                    channel = DescribeChannel(channel),
                    program = new
                    {
                        type = start.Program.ItemType,
                        title = start.Program.Title,
                        subtitle = start.Program.Subtitle,
                        start = start.Program.StartTs,
                        end = start.Program.EndTs,
                    },
                });
            }
            catch (JellyfinException ex)
            {
                return Results.Json(new { error = "jellyfin_error", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        signedIn.MapGet("/play/{playId}/info", async (string playId, PlaybackService playback, CancellationToken ct) =>
        {
            try
            {
                return await playback.GetInfoAsync(playId, ct) is { } info
                    ? Results.Ok(info)
                    : Results.NotFound(new { error = "not_found" });
            }
            catch (JellyfinException ex)
            {
                return Results.Json(new { error = "jellyfin_error", message = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Video players can't always attach a header to every request, so these
        // are protected by the long random ID in the URL, which only lasts while it's in use.
        open.MapGet("/play/{playId}/direct", PlaybackEndpoints.RelayDirectAsync);
        open.MapGet("/play/{playId}/{**path}", PlaybackEndpoints.RelayStreamAsync);
        open.MapDelete("/play/{playId}", async (string playId, PlaybackService playback, CancellationToken ct) =>
        {
            await playback.StopAsync(playId, ct);
            return Results.NoContent();
        });
    }

    private static object DescribeChannel(Channel channel) => new
    {
        channel.Id,
        channel.Number,
        channel.Name,
        channel.Color,
        logoUrl = channel.LogoVersion is { } version ? $"{Prefix}/channels/{channel.Id}/logo?v={version}" : null,
    };

    private static IResult TokenError(string code) => Results.BadRequest(new { error = code });
}
