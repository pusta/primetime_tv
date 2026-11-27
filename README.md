# PrimeTime TV

Turn your Jellyfin server into a broadcast-style TV service.

PrimeTime is a self-hosted service that sits next to [Jellyfin](https://jellyfin.org). It will build
24/7 channels from your Jellyfin library and play them on a schedule, with a channel guide, so you
can tune in and see whatever is "on" instead of picking something to watch. Native apps for iPhone,
iPad and Apple TV come later.

> **Status: early.** PrimeTime runs in Docker, walks you through a one-time setup, connects to a
> Jellyfin server, indexes the movies and shows in the libraries you choose, schedules channels into
> a TV guide, and plays them in the browser. The native apps are in progress.

## Deploying

PrimeTime runs as a single container. The prebuilt image, for `linux/amd64` (most PCs and servers)
and `linux/arm64` (Apple Silicon, Raspberry Pi 4/5 and other ARM boards), is published at:

```
ghcr.io/pusta/primetime_tv
```

You need Docker with Compose (Docker Desktop, OrbStack, or Docker Engine on Linux) and a Jellyfin
server that the machine running PrimeTime can reach. PrimeTime doesn't transcode, so it needs no GPU
and very little CPU; viewers' video streams pass through it, so give it a decent network connection.

### Option 1: Docker Compose (recommended)

Download the compose file and the example settings into a folder of their own, then start it:

```bash
mkdir primetime && cd primetime
curl -fsSLO https://raw.githubusercontent.com/pusta/primetime_tv/main/docker-compose.yml
curl -fsSL https://raw.githubusercontent.com/pusta/primetime_tv/main/.env.example -o .env
docker compose up -d
```

Open `http://<this machine's address>:8080` and follow [First-time setup](#first-time-setup).

Before you start, or any time after, edit `.env` to change the settings. The ones most worth setting:

- `TZ`: your time zone, for example `America/New_York`, so log times are local. (The guide always
  shows times in each viewer's own time zone.)
- `PRIMETIME_HTTP_PORT`: if something else already uses port 8080.
- `PRIMETIME_PUBLIC_URL`: the address your phone and TV will use to reach PrimeTime, for example
  `http://192.168.1.20:8080`. Sign-in QR codes for the apps point here.

Run `docker compose up -d` again after changing `.env`. All settings are listed under
[Configuration](#configuration).

### Option 2: `docker run`

The same thing without a compose file:

```bash
docker run -d --name primetime --restart unless-stopped \
  -p 8080:8080 \
  -v primetime-data:/data \
  -e TZ=America/New_York \
  -e PRIMETIME_PUBLIC_URL=http://192.168.1.20:8080 \
  --add-host host.docker.internal:host-gateway \
  ghcr.io/pusta/primetime_tv:latest
```

### Option 3: next to Jellyfin in the same compose file

If Jellyfin already runs from a compose file, you can add PrimeTime to it. Both containers then share
a network, and PrimeTime reaches Jellyfin by its service name:

```yaml
services:
  jellyfin:
    # ...your existing Jellyfin service...

  primetime:
    image: ghcr.io/pusta/primetime_tv:latest
    container_name: primetime
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      TZ: America/New_York
      PRIMETIME_PUBLIC_URL: http://192.168.1.20:8080
    volumes:
      - primetime-data:/data

volumes:
  primetime-data:
```

During setup, use `http://jellyfin:8096` as the Jellyfin URL (or whatever the service is called).
The same compose snippet works as a stack in Portainer or Dockge.

### Which Jellyfin URL to use

PrimeTime connects to Jellyfin from inside its container, so `localhost` means the container, not
your computer.

| Jellyfin runs…                         | Use                                       |
| -------------------------------------- | ----------------------------------------- |
| on the same computer, outside Docker   | `http://host.docker.internal:8096`        |
| in Docker, in the same compose project | `http://jellyfin:8096` (its service name) |
| on another machine on your network     | `http://192.168.x.x:8096` or its hostname |

If Jellyfin uses a base URL (for example `/jellyfin`), include it.

### Choosing a version

`PRIMETIME_IMAGE_TAG` in `.env` (or the tag after `:` in the image name) picks what you run:

| Image tag      | What it is                                                     |
| -------------- | -------------------------------------------------------------- |
| `latest`       | the newest release (default)                                   |
| `0.1.0`, `0.1` | a specific release, or the newest patch of a release line      |
| `edge`         | the newest build of the `main` branch; may be unstable         |

Pin a version such as `0.1` if you'd rather update on your own schedule.

### Updating

```bash
docker compose pull
docker compose up -d
```

With `docker run`, pull the new image, then remove the container and run the same command again.
Your data lives in the `primetime-data` volume, so it carries over. Database changes are applied
automatically when the new version starts. Moving back to an older version after that isn't
supported, so back up first.

### Backing up and restoring

Everything PrimeTime keeps (admin account, Jellyfin connection, channels, schedules, logos and the
keys that encrypt the Jellyfin credentials) is in the `/data` volume. To back it up from the folder
with your compose file:

```bash
docker compose stop
docker run --rm --volumes-from primetime -v "$PWD":/backup alpine \
  tar czf /backup/primetime-data.tgz -C /data .
docker compose start
```

To restore, stop PrimeTime, unpack the archive into the volume, and start it again:

```bash
docker compose stop
docker run --rm --volumes-from primetime -v "$PWD":/backup alpine \
  sh -c 'rm -rf /data/* && tar xzf /backup/primetime-data.tgz -C /data'
docker compose start
```

The backup holds the encryption keys alongside the database, so store it as carefully as a password.

### Reaching it from outside your home

PrimeTime serves plain HTTP. To use it away from home, either reach it over a VPN (such as Tailscale
or WireGuard), or put it behind a reverse proxy that adds HTTPS. With a proxy:

1. Point the proxy at PrimeTime's port. For example, with Caddy:

   ```
   primetime.example.com {
       reverse_proxy 192.168.1.20:8080
   }
   ```

2. Set `PRIMETIME_TRUSTED_PROXIES` to the proxy's IP address (or its Docker network's range, such as
   `172.18.0.0/16`, if the proxy runs in Docker), so PrimeTime trusts its `X-Forwarded-*` headers.
3. Set `PRIMETIME_PUBLIC_URL` to the HTTPS address, for example `https://primetime.example.com`.

Video passes through the proxy too, so make sure it doesn't buffer or time out long responses.

### Useful commands

```bash
docker compose logs -f primetime     # follow the logs
curl http://localhost:8080/health    # health check
docker compose ps                    # running state, including the health check
docker compose down                  # stop and remove the container (data is kept)
docker compose down -v               # stop and delete all PrimeTime data
```

### Building from source

To build the image yourself instead of pulling it:

```bash
git clone https://github.com/pusta/primetime_tv.git
cd primetime_tv
cp .env.example .env
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```

## First-time setup

Open `http://<this machine's address>:8080` (or the port you set as `PRIMETIME_HTTP_PORT`). Do this
straight away: until the admin account exists, anyone who can reach the port can create it.

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
the channels PrimeTime suggests from your library, or choose **Build your own**. Suggestions include
your collections; genres, decades and combinations like 80s Action; themes from Jellyfin's tags, such
as Superhero Movies; favorite directors and stars; family and kids channels; the best rated; short and
long watches; recent arrivals; seasonal channels; networks; and single-show marathons. Each one is
counted against your library with the same rules a channel uses, and ones with too few titles are left
out. To build your own:

- Pick **Movies** or **Shows**.
- **By category** includes everything matching a year range, genres and studios or networks. "Before
  year" isn't included, so from 1980, before 1990 means the 1980s. **More filters** adds age ratings,
  a minimum score, length (a typical episode, for shows), how recently titles were added, themes and
  keywords (Jellyfin's tags, titles and descriptions), and people (directors, creators and lead
  actors). Everything you set has to match. New matching titles join by themselves.
- **Choose titles** plays only the movies or shows you select.
- **Like these titles** starts from a few you choose and adds the ones in your library most like them,
  going by genres, themes, people, era and descriptions. Shared rare details count for more than
  common ones, so two heist films starring the same actor match more closely than two dramas. It all
  runs on the server, with no model to download, and new arrivals join if they fit.
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
collections changed. Use the pencil next to a channel in the Guide to edit it.

Directors and lead actors are read during the library scan, so run a scan after updating before
building people channels.

Each channel can have a logo, shown in place of its name in the Guide. Add one in the channel builder: a
PNG, JPEG, WebP or GIF image up to 2 MB.

Programs in the Guide take their channel's color. Choose one in the channel builder, or leave it on
Automatic to get a color that suits the channel's genre and stands apart from your other channels.

Channels survive disconnecting and reconnecting the same Jellyfin server. Connecting a different
server removes them, because its items are different.

## Watching

The top of the Guide shows the program you've picked: its channel, times, genre, length and rating.
It starts on what's on now. Click any program in the grid to see its details there, and **Watch Live**
(or click the program again, if it's on now) to tune in. Selecting a channel's name tunes in too.
You join where the schedule says the channel is now, as with live TV. **Start from Beginning** rewinds to the start of the current program and **Back to Live**
returns to the live position. **Info** shows the description, cast and other details Jellyfin has for
the program. When a program ends the channel moves on to whatever's airing next.
Use the pencil next to a channel to edit it.

Jellyfin does all the work on the video. PrimeTime asks it for an HLS stream, and Jellyfin decides what
to transcode and what to pass along as it is, so PrimeTime never needs FFmpeg or a GPU. The stream does
pass through PrimeTime on its way to your browser, which keeps your Jellyfin API key on the server and
means viewers only need to reach PrimeTime. It uses that machine's network bandwidth, not its CPU.

## Streams

*Settings → Streams* shows what's playing right now: the channel and program, who's watching and on
what (a browser or a linked app, and its network address), whether Jellyfin is transcoding it or just
repackaging it (hover over **Transcoding** to see why), the video format, and how much has been sent.
A stream is **Paused** when its player hasn't asked for video for 30 seconds; after 5 minutes it's
let go. **Stop** ends a stream, and the viewer's player says it was stopped instead of tuning in again.

Below that are the last 10 streams that finished, with how long they ran and how they ended. Each
program is its own stream, so watching a channel through several programs adds several entries.

## Apps

The server is ready for the native tvOS, iOS and iPadOS apps; their API is in [docs/app-api.md](docs/app-api.md).
An app signs in by showing a QR code. Scan it, sign in to PrimeTime on your phone, and approve the
device. Set `PRIMETIME_PUBLIC_URL` so the QR code points at the address your phone can reach. Linked apps can watch but can't change settings. See or remove them under
*Settings → Devices*. Changing your password signs them out too.

## Scheduled tasks

*Settings → Scheduled Tasks* controls background jobs: the **Library scan**, every 4 hours by
default, and the **Guide schedule**, hourly by default, which keeps channels scheduled a week ahead.
You can change either interval (15 minutes to 30 days), turn automatic runs off, start a run, stop
one, and see the last 10 runs.

The next run is counted from the end of the last one. Saving a shorter interval never starts a scan
straight away. If PrimeTime was stopped when a scan was due, or was restarted partway through one,
it scans again as soon as it's back up.

## Configuration

Set these in `.env` (see [.env.example](.env.example)):

| Variable              | Default       | What it does                                        |
| --------------------- | ------------- | --------------------------------------------------- |
| `PRIMETIME_IMAGE_TAG` | `latest`     | Which published image to run: `latest`, a version, or `edge` |
| `PRIMETIME_HTTP_PORT` | `8080`        | Port on your machine for the web UI and API         |
| `TZ`                  | `UTC`         | Time zone for logs, for example `America/New_York`  |
| `PRIMETIME_LOG_LEVEL` | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error` |
| `PRIMETIME_TRUSTED_PROXIES` | *(empty)* | IP addresses or CIDR ranges of reverse proxies to trust |
| `PRIMETIME_PUBLIC_URL` | *(empty)* | The address people reach PrimeTime at; QR codes for the apps link here |

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
├── .github/workflows/docker.yml  # builds the image and publishes it to ghcr.io
├── docker-compose.yml        # runs the published image; SQLite data lives in a named volume
├── docker-compose.dev.yml    # override that builds the image from this checkout instead
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

## Publishing images

[.github/workflows/docker.yml](.github/workflows/docker.yml) builds the image for `amd64` and
`arm64` and pushes it to GitHub Container Registry:

- every push to `main` publishes `:edge` and `:sha-<commit>`
- a version tag publishes `:1.2.3`, `:1.2` and `:latest`:
  `git tag v1.2.3 && git push origin v1.2.3`
- pull requests build the image to check it, without publishing

The workflow signs in with the repository's built-in `GITHUB_TOKEN`, so no secrets are needed.

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

The web player includes [hls.js](https://github.com/video-dev/hls.js), under the Apache License 2.0
(`server/PrimeTime.Server/wwwroot/vendor/hls.js-LICENSE`).
