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
docker volume create rankoon-data-protection
docker run -d \
  --name rankoon \
  --restart unless-stopped \
  --env-file .env \
  -v rankoon-data-protection:/var/lib/rankoon/data-protection-keys \
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

## Data Protection Key Ring

`DATAPROTECTION__KEYRINGPATH` is required outside the `Development` environment.
Rankoon validates it during startup by creating and removing a probe file. The
directory must be readable and writable by the application user, persistent
across image replacement, and outside `/app`; `/app` is part of the image and
is not a safe key-ring location. The application name is fixed as `Rankoon`, so
all instances sharing this key ring can decrypt the same protected data.

The Docker images declare `/var/lib/rankoon/data-protection-keys` as the
key-ring volume. Use the named-volume mount in the run command above. For a
bind mount or a network volume, grant its directory read/write access to the
non-root user configured by the image before starting Rankoon. Do not run the
application as root merely to bypass a permission failure.

All replicas of one Rankoon deployment must mount the same key ring with
read/write access. A replica using a different ring cannot decrypt custom bot
tokens written by the others; those tokens fail safely during unprotection and
must not be replaced by an empty or newly generated ring. Do not share a ring
between unrelated deployments.

Back up the complete key-ring directory as an encrypted, access-controlled
unit before upgrades and test restoring it before relying on the backup. Keep
the backup for at least as long as protected custom-bot tokens can exist. If
the key ring is lost, destroyed, or replaced, existing protected values cannot
be recovered. Restore the original ring to recover them; otherwise affected
custom bot tokens must be entered again by their owners. Never place key-ring
files in source control or expose them through a web-server mount.

Development is the sole environment allowed to run without a configured key
ring, using ASP.NET Core's local fallback. This fallback is intentionally
ephemeral and must not be used for custom-bot tokens that need to survive a
restart or deployment.

## MongoDB Startup

At startup Rankoon retries index initialization until MongoDB is available. It
creates unique identities for member XP, discrete ledger grant keys, voice sessions,
season settings and sequences, active seasons, final standings, and self-role
assignments, plus ranking, projection, report-query, and TTL indexes. Compressed
voice uses `voice_activity_days` with unique `(guild_id, user_id, day_start_utc,
part)`, session-cursor, open-projection, and guild-period indexes. Migration state
is stored in `voice_ledger_migration_states` with a phase/update index.

The resumable legacy voice migration copies a fixed ledger high-water mark in
`VoiceActivity__MigrationBatchSize` batches unless
`VoiceLedgerMigration__CopyBatchSize` explicitly overrides it. It records generated
documents/segments and verifies exact global, guild/user, season, and
guild/user/season XP and duration parity. During `Copying`, `Verifying`, or
`ParityFailed`, audit history reads legacy voice ledger rows plus only new compressed
segments. Successful parity changes authority to compressed days in
`AwaitingDeletionApproval`; `Deleting` requires explicit approval of that parity
fingerprint, and `Completed` records completion. A mismatch cannot authorize
deletion.

Rankoon supports standalone MongoDB and does not require replica-set transactions.
Voice accrual, projection leases, cursor-based migration, and repair use idempotent
single-document operations and compare-and-swap updates. Compatible startup
migrations also remove the obsolete voice holdback setting, initialize missing
member leaderboard fields and totals, and set missing final-standing visibility to
public. There is no separate relational migration command.
