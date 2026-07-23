# Deployment

The published image contains the Angular frontend and the ASP.NET Core backend. It exposes one HTTP port (`8080`); frontend and API are served from the same origin, with the API below `/api`.

## GitHub Container Registry

`.github/workflows/release.yml` runs for pushes to `main` and `dev`. It calls
`.github/workflows/ci.yml`, which tests both applications and uploads their build
artifacts. `Backend/Dockerfile.deploy` packages these artifacts without rebuilding
the source.

Successful `main` builds publish `latest` and the prospective semantic version.
When semantic-release creates a release, major and minor tags are added. Successful
`dev` builds use a prospective version plus the short commit SHA, for example
`0.4.2-1a2b3c4d`, and publish the same image as both that version and `latest-dev`.

The workflow can also be started manually. The running application version is available without authentication at `GET /api/version`.

After the first publication, set the package visibility to **Public** in the GitHub package settings so it can be pulled without authentication.

## Run

Create an environment file from `deploy/.env.example`, fill in the values, then start the image:

```sh
docker run -d \
  --name rankoon \
  --restart unless-stopped \
  --env-file .env \
  -p 8080:8080 \
  ghcr.io/<owner>/<repository>:latest
```

`MONGODB_CONNECTION_STRING` must point to a reachable MongoDB instance. It can be
another container, but Rankoon itself is delivered as exactly one container.

## Configuration

The application follows ASP.NET Core environment-variable naming: nested configuration keys use a double underscore (`__`). The required variables are listed in `deploy/.env.example`.

`DISCORD_REDIRECT_URI` must be registered as the Discord OAuth redirect URL.
`FRONTEND_BASE_URL` is the externally reachable URL of this container. If a
reverse proxy terminates TLS, these values must still use the public `https` URL.
OAuth callback tokens are returned in a URL query string; prevent proxies and
logs from retaining full callback URLs.

Optional settings from `Backend/appsettings.json` can be overridden using the same convention, for example `Jwt__Issuer`, `Jwt__Audience`, or `Serilog__MinimumLevel__Default`. The container listens on port `8080` by default; override `ASPNETCORE_URLS` only when a different in-container port is required.

## MongoDB Startup

At startup Rankoon retries index initialization until MongoDB is available. It
creates unique identities for member XP, ledger grant keys, voice sessions, season
settings and sequences, active seasons, final standings, and self-role assignments,
plus ranking, projection, report-query, and TTL indexes. Compatible startup
migrations remove the obsolete voice holdback setting,
initialize missing member leaderboard fields and totals, and set missing
final-standing visibility to public. There is no separate manual migration command.
