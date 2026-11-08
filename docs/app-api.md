# PrimeTime app API (v1)

For the tvOS, iOS and iPadOS apps. Every route is under `/api/app/v1`, on the server URL the user
enters. Times are Unix seconds. Errors are JSON with an `error` code.

## Checking a server

`GET /server` needs no sign-in:

```json
{ "name": "PrimeTime", "apiVersion": 1, "ready": true, "publicUrl": "https://primetime.example.com" }
```

`ready` is false until the admin has finished setup on the website. Until then, `POST /device/start` returns
`409 server_not_set_up`. `publicUrl` is the address the admin set in `PRIMETIME_PUBLIC_URL`, or `null`.

## Signing in with a QR code

1. `POST /device/start` with `{ "deviceName": "Living Room TV", "platform": "tvOS" }`:

   ```json
   {
     "deviceCode": "<secret, keep private>",
     "userCode": "BCDF-GHJK",
     "verificationUri": "http://server:8080/#/link",
     "verificationUriComplete": "http://server:8080/#/link?code=BCDF-GHJK",
     "expiresIn": 600,
     "interval": 5
   }
   ```

2. Show `verificationUriComplete` as a QR code. Also show `userCode` and `verificationUri`, for anyone
   who'd rather type them. The URLs use the server's `publicUrl`, or if that isn't set, the address the
   app connected with.
3. The user scans it, signs in on the website if needed, and approves the device.
4. Every `interval` seconds, `POST /device/token` with `{ "deviceCode": "..." }`. A `400` has one of:

   | `error`                 | Meaning                                         |
   | ----------------------- | ----------------------------------------------- |
   | `authorization_pending` | Not approved yet; keep checking                 |
   | `slow_down`             | Checked too soon; wait longer before the next   |
   | `access_denied`         | The user cancelled; stop                        |
   | `expired_token`         | Took over 10 minutes; start again               |
   | `invalid_grant`         | Unknown or already-used code; start again       |

   Success returns `{ "accessToken": "ptd_...", "tokenType": "Bearer" }`, once only. Store it in the Keychain.

Send `Authorization: Bearer <token>` on the routes below. A `401` means the token is no longer valid:
the device was removed under *Settings → Devices*, or the admin changed their password. Go back to step 1.

App tokens can only watch. They can't reach the admin API, and web sign-in cookies don't work here.

## Routes that need a token

| Route                              | Purpose                                             |
| ---------------------------------- | --------------------------------------------------- |
| `GET /me`                          | `deviceId` and `deviceName`; a quick token check    |
| `POST /signout`                    | Revokes this device's token                         |
| `GET /channels`                    | Channels: `id`, `number`, `name`, `color`, `logoUrl` |
| `GET /channels/{id}/logo?v=`       | The logo image. The URL changes when the logo does  |
| `GET /guide?from=&to=`             | Programs per channel, for up to 48 hours            |
| `POST /channels/{id}/play`         | Tune in; see below                                  |
| `GET /play/{playId}/info`          | Description, cast and other details                 |

`logoUrl` is `null` for a channel with no logo, and needs the token, so fetch it with `URLSession`.
`color` is a hex string such as `#3d9be9`.

## Playing a channel

`POST /channels/{id}/play` with `{ "player": "mpv" }`, or `"native"` for `AVPlayer`:

```json
{
  "playId": "...",
  "hlsUrl": "/api/app/v1/play/.../master.m3u8",
  "directUrl": "/api/app/v1/play/.../direct",
  "now": 1794171000,
  "offsetSeconds": 1560,
  "channel": { "id": 1, "number": 1, "name": "Movies", "color": "#3d9be9", "logoUrl": null },
  "program": { "type": "movie", "title": "...", "subtitle": "1997", "start": 1794169440, "end": 1794174840 }
}
```

`409 nothing_on_air` means nothing is scheduled right now. `502 jellyfin_error` includes a `message`.

The stream URLs are relative to the server and need no token, because the long random ID is the
credential. Treat them as secrets, and don't log them.

Which URL to use:

- **`hlsUrl`**: the same way the website plays. Jellyfin passes along what the player can decode and
  transcodes the rest. With `"mpv"`, H.264, HEVC, AAC, AC3, E-AC3 and MP3 pass through untouched.
- **`directUrl`**: the original file, with byte-range seeking and no Jellyfin transcoding at all. Only
  for mpv, and the best choice for formats Jellyfin struggles to convert. Try this first with mpv and
  fall back to `hlsUrl`.

**Join live:** start at `offsetSeconds` (with mpv, `--start=<offset>`). Compute the offset again from
`now` if there's a delay before playback begins.

**Start from Beginning:** seek to `0`. **Back to Live:** seek to `now - program.start`, adjusted for the
time since you tuned in.

**Next program:** when the program's `end` passes, stop and call `play` again. Each program has its own
`playId`, so don't reuse an old one.

**Stop:** `DELETE /play/{playId}` when leaving (no token needed), so Jellyfin stops encoding. A stream
with no requests for 5 minutes is cleaned up automatically, and then returns `404`. A paused player
should call `play` again when it resumes after that long.

## App Transport Security

A home PrimeTime server usually runs on plain HTTP, so the app needs `NSAllowsLocalNetworking` for
LAN addresses, plus the Local Network usage description on iOS and iPadOS.
