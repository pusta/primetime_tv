# PrimeTime TV

Turn your Jellyfin server into a broadcast-style TV service.

PrimeTime is a self-hosted service that sits next to [Jellyfin](https://jellyfin.org). It will build
24/7 channels from your Jellyfin library and play them on a schedule, with a channel guide, so you
can tune in and see whatever is "on" instead of picking something to watch. Native apps for iPhone,
iPad and Apple TV come later.

> **Status: early.** Right now PrimeTime runs in Docker, walks you through a one-time setup, connects
> to a Jellyfin server, and keeps an index of the movies and shows in the libraries you choose. The
> Guide page is a placeholder for now. Channels, scheduling, the guide and playback come next.

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

## Scheduled tasks

*Settings → Scheduled Tasks* controls background jobs. For now that's the **Library scan**, which
runs every 4 hours by default. You can change the interval (15 minutes to 30 days), turn automatic
runs off, start a run, stop one, and see the last 10 runs.

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

Everything else (admin account, Jellyfin connection) is set in the web UI and stored in the
`primetime-data` Docker volume.

## Health endpoint

`GET /health` needs no login. It returns `200` when the service and its database are working,
and `503` when the database isn't:

```json
{ "status": "healthy", "version": "0.1.0", "checks": { "database": "ok" } }
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
  Treat the volume like a secret.
- Sign-in uses an HTTP-only, `SameSite=Strict` cookie. The login and setup endpoints are
  rate-limited to 10 attempts per minute per IP address.
- PrimeTime serves plain HTTP. If you expose it beyond your home network, put it behind a reverse
  proxy that adds HTTPS.

## Project layout

```
.
├── docker-compose.yml        # runs the backend; SQLite data lives in a named volume
├── Dockerfile                # two-stage .NET build → small ASP.NET runtime image, non-root
├── .env.example              # settings for docker compose
├── server/
│   └── PrimeTime.Server/     # backend API and web management UI
│       ├── Program.cs        # startup: services, auth, middleware, routes
│       ├── Endpoints/        # HTTP routes: /health, auth, /api/jellyfin, /api/libraries, /api/tasks
│       ├── Jellyfin/         # Jellyfin API client and connection logic
│       ├── Library/          # library selection and the scan task
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
