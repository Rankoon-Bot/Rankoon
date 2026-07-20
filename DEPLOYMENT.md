# Deployment

The published image contains the Angular frontend and the ASP.NET Core backend. It exposes one HTTP port (`8080`); frontend and API are served from the same origin, with the API below `/api`.

## GitHub Container Registry

The workflow in `.github/workflows/publish-container.yml` publishes `ghcr.io/<owner>/<repository>` for every push to `main` and for version tags beginning with `v`. The default-branch image additionally receives the `latest` tag. Semantic version tags also receive version and major/minor tags without the leading `v`.

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

`MongoDb__ConnectionString` must point to a reachable MongoDB instance. It can be another container, but Rankoon itself is delivered as exactly one container.

## Configuration

The application follows ASP.NET Core environment-variable naming: nested configuration keys use a double underscore (`__`). The required variables are listed in `deploy/.env.example`.

`Discord__RedirectUri` must be registered as the Discord OAuth redirect URL. `Frontend__BaseUrl` is the externally reachable URL of this container. If a reverse proxy terminates TLS, these values must still use the public `https` URL.

Optional settings from `Backend/appsettings.json` can be overridden using the same convention, for example `Jwt__Issuer`, `Jwt__Audience`, or `Serilog__MinimumLevel__Default`. The container listens on port `8080` by default; override `ASPNETCORE_URLS` only when a different in-container port is required.
