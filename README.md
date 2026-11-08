# PrimeTime TV

Turn your Jellyfin server into a broadcast-style TV service.

PrimeTime is a self-hosted service that sits next to [Jellyfin](https://jellyfin.org). It will build
24/7 channels from your Jellyfin library and play them on a schedule, with a channel guide, so you
can tune in and see whatever is "on" instead of picking something to watch. Native apps for iPhone,
iPad and Apple TV come later.

> **Status: early.** Right now PrimeTime runs in Docker, walks you through a one-time setup, connects
> to a Jellyfin server, keeps an index of the movies and shows in the libraries you choose, and
> schedules channels into a TV guide. Playback comes next.

## Quick start

You need Docker with Compose (Docker Desktop, OrbStack, or Docker Engine on Linux).

```bash
git clone git@github.com:pusta/primetime_tv.git
cd primetime_tv
cp .env.example .env        # optional: change the port, time zone or log level
docker compose up --build
```

Open <http://localhost:8080> (or the port you set as `PRIMETIME_HTTP_PORT`).

1. **Create the admin account.** The first visit asks for a username and password. This is the
   PrimeTime admin login, separate from your Jellyfin users.
2. **Connect Jellyfin.** This starts a one-time setup. Enter the server URL and choose how PrimeTime
   signs in:
   - **API key** (recommended): in Jellyfin, open *Dashboard → API Keys*, create a key named
     "PrimeTime", and paste it in.
   - **Username and password**: PrimeTime signs in once, keeps the access token Jellyfin returns,
     and doesn't store the password. An administrator account sees each library's folder paths; a
     regular account sees just the libraries it has access to.
3. **Test connection** checks the details without saving them. **Save and continue** checks them
   again and stores them.
4. **Choose your libraries.** Tick the libraries to include and choose **Confirm and scan**. Movie,
   show and mixed libraries can be indexed.
5. **Wait for the first scan.** When it finishes, PrimeTime opens the **Guide** page. If every
   library fails to scan, you can try again or change your selection from the same screen.

Setup only happens once. After that, the connection and libraries are managed under
*Settings → Library*, even if you disconnect Jellyfin later.

## The library index

A scan reads each selected library from Jellyfin and stores its movies, shows and episodes in
PrimeTime's database, ready for building channels. Episodes that Jellyfin knows about but has no
file for are left out, since they can't be played.

Each scan also removes items that are gone from Jellyfin. If a library fails to scan, or Jellyfin
returns fewer items than it reported, PrimeTime keeps what it has for that library and tries again
on the next scan.

**Change libraries** under *Settings → Library* updates your selection. Leaving a library out removes its
items from the index. Connecting a different Jellyfin server, or disconnecting, clears the selection
and the whole index.

## Channels and the Guide

The **Guide** shows what's on each channel, up to 7 days ahead. Choose **Add channel** to add one of
the channels PrimeTime suggests from your library (your collections, or by genre, decade, network, or
a single-show marathon), or choose **Build your own**:

- Pick **Movies** or **Shows**.
- **By category** includes everything matching a year range, genres and studios or networks. "Before
  year" isn't included, so from 1980, before 1990 means the 1980s. New matching titles join by
  themselves.
- **Choose titles** plays only the movies or shows you select.
- **From collections** plays whatever is in the Jellyfin collections you select, so you can manage a
  channel's content from Jellyfin. Include **Collections** in your libraries under *Settings → Library*
  to use this. Changes reach the channel after the next library scan. Only items in your selected
  movie and show libraries can play, and a channel plays one type of content, so a collection
  holding both movies and shows contributes just the type you chose.

Programs run back-to-back. Each channel has an **Air order**: by release or air date, oldest first or
newest first, or random. On a channel with several shows, the order applies to all their episodes
together. Everything plays once before any repeat. Items with no known runtime and season 0 specials
are left out.

Each schedule is stored ahead of time, so what's on doesn't move when the library changes. Editing a
channel replaces what hasn't started yet, and whatever is on air finishes first. Changing the air
order starts again from the top of the new order. After a scan, a
channel is rescheduled only if something it was going to play has been removed or one of its
collections changed. Select a channel's
name in the Guide to edit it.

Each channel can have a logo, shown in place of its name in the Guide. Add one in the channel builder: a
PNG, JPEG, WebP or GIF image up to 2 MB.

Programs in the Guide take their channel's color. Choose one in the channel builder, or leave it on
Automatic to get a color that suits the channel's genre and stands apart from your other channels.

Channels survive disconnecting and reconnecting the same Jellyfin server. Connecting a different
server removes them, because its items are different.

## Scheduled tasks

*Settings → Scheduled Tasks* controls background jobs: the **Library scan**, every 4 hours by
default, and the **Guide schedule**, hourly by default, which keeps channels scheduled a week ahead.
You can change either interval (15 minutes to 30 days), turn automatic runs off, start a run, stop
one, and see the last 10 runs.

The next run is counted from the end of the last one. Saving a shorter interval never starts a scan
straight away. If PrimeTime was stopped when a scan was due, or was restarted partway through one,
it scans again as soon as it's back up.

### Which URL to use

PrimeTime connects to Jellyfin from inside its container, so `localhost` means the container, not
your computer.

| Jellyfin runs…                         | Use                                       |
| -------------------------------------- | ----------------------------------------- |
| on the same computer, outside Docker   | `http://host.docker.internal:8096`        |
| in Docker, in the same compose project | `http://jellyfin:8096` (its service name) |
| on another machine on your network     | `http://192.168.x.x:8096` or its hostname |

If Jellyfin uses a base URL (for example `/jellyfin`), include it.

### Useful commands

```bash
docker compose logs -f primetime     # follow the logs
curl http://localhost:8080/health    # health check
docker compose down                  # stop (data is kept)
docker compose down -v               # stop and delete all PrimeTime data
```

## Configuration

Set these in `.env` (see [.env.example](.env.example)):

| Variable              | Default       | What it does                                        |
| --------------------- | ------------- | --------------------------------------------------- |
| `PRIMETIME_HTTP_PORT` | `8080`        | Port on your machine for the web UI and API         |
| `TZ`                  | `UTC`         | Time zone for logs (and, later, channel schedules)  |
| `PRIMETIME_LOG_LEVEL` | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error` |
| `PRIMETIME_TRUSTED_PROXIES` | *(empty)* | IP addresses or CIDR ranges of reverse proxies to trust |

Everything else (admin account, Jellyfin connection) is set in the web UI and stored in the
`primetime-data` Docker volume.

## Health endpoint

`GET /health` needs no login. It returns `200` when the service and its database are working,
and `503` when the database isn't:

```json
{ "status": "healthy", "checks": { "database": "ok" } }
```

The container's Docker `HEALTHCHECK` calls it too.

## How credentials are stored

- **Admin passwords** are hashed with PBKDF2-SHA256 (600,000 iterations, random salt).
- **The Jellyfin API key or access token** is encrypted with ASP.NET Core Data Protection before it
  goes into the database, and it's never sent back to the browser. A Jellyfin password is never
  stored; it's swapped for an access token at sign-in.
- **Credentials stay out of logs.** They travel only in request bodies and the `Authorization`
  header, never in URLs, and PrimeTime doesn't log request bodies or headers.
- The encryption keys live in the same data volume (`/data/keys`). That protects against a leaked
  database file or backup of just the database, not against someone who has the whole volume.
  Treat the volume, and any backup of it, like a secret.
- **Create the admin account straight away.** Until it exists, anyone who can reach the port can
  create it. Docker publishes the port on every network interface, so keep PrimeTime off untrusted
  networks until setup is done.
- Sign-in uses an HTTP-only, `SameSite=Strict` cookie tied to a session on the server, so signing
  out ends it even if someone copied the cookie. Changing your password under *Settings → Account*
  signs out your other devices. The login, setup and password endpoints are rate-limited to 10
  attempts per minute per IP address.
- PrimeTime serves plain HTTP. If you expose it beyond your home network, put it behind a reverse
  proxy that adds HTTPS, and set `PRIMETIME_TRUSTED_PROXIES` to the proxy's IP address or CIDR range
  (comma-separated if there are several). Without it PrimeTime ignores the proxy's `X-Forwarded-*`
  headers, so the sign-in cookie isn't marked `Secure` and everyone shares one login rate limit.
  Only list addresses you control: a listed address can claim to be any client.

## Project layout

```
.
├── docker-compose.yml        # runs the backend; SQLite data lives in a named volume
├── Dockerfile                # two-stage .NET build → small ASP.NET runtime image, non-root
├── .env.example              # settings for docker compose
├── server/
│   └── PrimeTime.Server/     # backend API and web management UI
│       ├── Program.cs        # startup: services, auth, middleware, routes
│       ├── Endpoints/        # HTTP routes: /health, auth, /api/jellyfin, /api/libraries, /api/tasks,
│       │                     #   /api/channels, /api/catalog, /api/guide
│       ├── Jellyfin/         # Jellyfin API client and connection logic
│       ├── Library/          # library selection and the scan task
│       ├── Guide/            # channel rules, schedule generator, suggestions
│       ├── Tasks/            # background task runner and scheduler
│       ├── Data/             # SQLite access, schema migrations, stores
│       ├── Security/         # password hashing
│       └── wwwroot/          # management UI (plain HTML, CSS and JS; no build step)
└── apps/
    └── apple/                # future native app for iOS, iPadOS and tvOS (not started)
```

## Why .NET and SQLite

- **ASP.NET Core (.NET 10)** gives a fast, well-supported HTTP server with cookie auth, rate
  limiting, an HTTP client and encryption (Data Protection) built in. The backend has one NuGet
  dependency, `Microsoft.Data.Sqlite`. Jellyfin itself is a .NET project, so its API models and
  conventions map over directly. It publishes to a ~100 MB runtime image that runs as non-root.
- **SQLite** keeps it to a single container: no separate database server to run, back up or
  upgrade. A self-hosted service with one household's channels and schedules is well within what
  it handles. If that ever stops being true, the data layer is small enough to move to Postgres.
- **A plain HTML/JS management UI** served by the backend means no Node toolchain or front-end
  build step. It can grow into a real front-end project when the UI needs it.

## Developing without Docker

With the [.NET 10 SDK](https://dotnet.microsoft.com/download) installed:

```bash
cd server/PrimeTime.Server
dotnet run --urls http://localhost:8080
```

Local runs keep their data in `server/PrimeTime.Server/local-data/` (git-ignored). Set
`PRIMETIME_DATA_DIR` to put it somewhere else.

## License

[MIT](LICENSE)
