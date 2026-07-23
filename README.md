# Rankoon

## Level-up announcements

Level-up announcements are disabled by default, including for guilds with a legacy XP level-up channel. Administrators can configure a text channel and safe templates in **Level-Up-Nachrichten**. Rendering supports only the allowlisted tokens exposed by the API; template text cannot execute code. Discord sends use restrictive allowed mentions, so manually typed mentions, roles, `@everyone`, and `@here` never ping. A transition outbox separates XP projection from Discord delivery and retries transient failures; this is best-effort deduplication, not an exactly-once Discord guarantee. The bot needs access to the target text channel and permission to send messages; role delivery is only claimed after a successful role assignment. MEE6 imports never create announcements.

**Free, open-source leveling for communities that actually talk.**

Rankoon is a configurable Discord bot that rewards meaningful text and voice
activity, provides server rankings, and creates member-managed temporary voice
channels. It stays focused on leveling, voice activity, and community voice
management instead of trying to be an all-in-one moderation bot.

[![License: AGPL v3](https://img.shields.io/github/license/Rankoon-Bot/Rankoon)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Rankoon-Bot/Rankoon)](https://github.com/Rankoon-Bot/Rankoon/releases/latest)
[![Release and publish container](https://github.com/Rankoon-Bot/Rankoon/actions/workflows/release.yml/badge.svg)](https://github.com/Rankoon-Bot/Rankoon/actions/workflows/release.yml)

> Reward real participation, in text and voice.

## Why Rankoon?

- **Configurable activity XP:** reward messages, reactions, threads, scheduled
  event interest, and time spent in voice channels.
- **Voice XP that reflects participation:** optionally require another active
  human in the channel, exclude deafened members and AFK channels, and apply
  channel-specific multipliers.
- **MEE6-compatible progression:** use the MEE6 cumulative level curve and
  import an existing MEE6 JSON export without discarding XP earned in Rankoon.
- **Rankings and progress:** provide `/rank`, a Discord top-ten leaderboard,
  dashboard rankings, and a configurable public or members-only web leaderboard.
- **Join-to-create voice hubs:** create temporary voice channels when members
  join a hub, then let each recorded owner rename the channel, set its user
  limit, disconnect a member, or transfer ownership.
- **A practical administration dashboard:** configure XP, role rewards,
  leaderboard visibility, voice hubs, dashboard access, and activity reports.
- **Self-hostable by design:** run the Angular dashboard, ASP.NET Core API, and
  Discord gateway client in one Linux container backed by MongoDB.

Rankoon is licensed under the [GNU Affero General Public License v3.0](LICENSE).
The codebase contains no
premium tier or subscription-gated implementation: the core functionality in
this repository is free and open source.

## Project Status

Rankoon is under active development. The repository contains versioned releases,
automated container publishing, backend and frontend test suites, and a complete
self-hosting path. Some operational and community documentation is still being
built out; see [Current limitations](#current-limitations).

An invitation URL for an officially hosted public instance is not currently
published in this repository. Until one is documented, use the self-hosting
instructions below rather than trusting an unofficial invite link.

## Features

### XP and leveling

Rankoon records every non-zero XP grant in an additive MongoDB ledger before
projecting it into member, season, and guild-stat totals. Unique grant keys and
per-projection applied-key sets make retries idempotent. A background repair
worker resumes pending projections after interruptions between writes. The
MEE6-compatible cumulative curve calculates levels from the persistent total.

### XP history and adjustments

`XP-Verlauf & Korrekturen` / `XP History & Adjustments` is an XP audit feature,
not general user management. `xp-audit` permits reading the guild-scoped member
history; `xp-adjustments` additionally permits immutable manual corrections.
Corrections contain the actor and a reason in the permanent XP ledger, are never
edited or deleted, and are neutralized only by a linked reversal entry. Non-owner
actors cannot correct their own XP. The activity log receives a supplementary
event, while the ledger remains the audit record. No message contents are stored.

The following sources are implemented and independently configurable:

| Source | Default behavior |
| --- | --- |
| Messages | 5-50 XP based on message length, with a 60-second member cooldown |
| Reactions | 2 XP when a member adds a reaction, with a 30-second cooldown |
| Thread creation | 15 XP for creating a thread |
| Thread messages | 5 XP, with a 60-second cooldown |
| Scheduled events | 10 XP when marking interest; removed interest reverses the grant |
| Voice activity | 10 XP per minute after a 60-second minimum session |

Administrators can enable or disable the complete XP system or individual
sources, adjust point values and cooldowns, exclude roles/channels/categories,
and configure voice-channel multipliers. Channel multipliers currently apply to
voice XP.

Voice XP can be configured to:

- ignore the server AFK channel;
- require more than one qualifying human in the channel;
- exclude deafened members;
- respect excluded roles, channels, and categories;
- grant fractional XP according to eligible connected time.

Message, reaction, and thread-message cooldowns are acquired atomically on the
member record after a ledger entry is written. Cooldown-denied entries are retained
without projection, so retries cannot later award them. Reaction removal can
reverse its original award when enabled; scheduled-event interest removal also
creates an idempotent reversal. Reversals retain the original season attribution.

Voice sessions are reconciled every 5 seconds by default and settled when a member moves. Self-hosters can set the global `VOICEWATCHDOG__INTERVALSECONDS` environment variable; this is intentionally not configurable per guild.
Eligible intervals are split at persisted season boundaries, producing uniquely
keyed ledger grants per segment. The first qualifying settlement includes time
since joining, including the configured minimum-session interval.

### Seasons

Guilds can run manual or scheduled fixed-duration, monthly, quarterly,
semiannual, or annual seasons. Scheduled guilds keep a configurable number of
future seasons prepared. Each prepared season snapshots its settings so later
configuration changes do not rewrite its rules.

Activation initializes a baseline from zero, lifetime, or lifetime-percentage XP.
Optional carry-over from previous frozen standings applies once. Closing writes
immutable final standings with rank, total XP, level, message count, voice time,
and public visibility. A per-guild MongoDB lease prevents concurrent automation.

Level rewards are cumulative: Rankoon adds every configured reward role at or
below a member's level and removes configured rewards above it. The Rankoon bot
role must be above every reward role it manages.

### Rankings

Rankoon offers three views of member progress:

- `/rank` shows the invoking member's level, total XP, and next level threshold.
- `/leaderboard` shows the server's top ten current members in Discord.
- The web leaderboard supports a guild-specific alias, signed cursor pagination,
  public or members-only access, per-member public visibility, and jump-to-user
  navigation for authenticated guild members.

Web leaderboard entries include level, total XP, message count, and connected
voice time. New guild leaderboards default to members-only visibility.

### Temporary voice channels

Administrators create voice hubs in the dashboard. A hub can use an existing
join channel or ask Rankoon to create one, and supports:

- a target category;
- a temporary channel name template using `{username}` or `{user}`;
- a user limit and bitrate;
- a maximum number of channels owned by one member;
- enabled or disabled state.

When a non-bot member joins an enabled hub, Rankoon creates a channel, records
the member as its owner, and moves them into it. Empty temporary channels are
removed automatically. Missing configured hub channels are reconciled while the
bot is running.

### Dashboard and reports

The Angular dashboard uses Discord OAuth and supports English and German. Guild
owners can grant dashboard modules to Discord roles for:

- XP configuration;
- leaderboard configuration;
- voice hubs;
- reporting.

The reporting pages expose recorded guild activity, command usage, and bot
errors. Report events are retained in MongoDB for 90 days through a TTL index.
These reports are application activity records, not infrastructure monitoring or
distributed tracing.

## Slash Commands

Rankoon synchronizes these commands globally when the Discord client becomes
ready. Discord may take time to propagate global command changes.

| Command | Description |
| --- | --- |
| `/rank` | Show your level, total XP, and next cumulative level threshold |
| `/leaderboard` | Show the top ten current members by XP |
| `/voice action:name value:<name>` | Rename the temporary channel you own |
| `/voice action:limit value:<0-99>` | Change your temporary channel's user limit; `0` removes the limit |
| `/voice action:kick member:<member>` | Disconnect a selected member while managing your owned channel |
| `/voice action:transfer member:<member>` | Transfer the stored channel ownership to another member |

Command responses are ephemeral. The `/voice` command works only while the
invoking member is connected to the temporary channel recorded as theirs.

## Migrate from MEE6

The dashboard accepts a MEE6-style JSON export. The import:

- verifies that the export's guild ID matches the selected Discord server;
- replaces the member's imported MEE6 XP, message count, and display name;
- preserves XP subsequently earned through Rankoon and any stored manual
  adjustment;
- recalculates each imported member's total and queues membership reconciliation.

The expected shape is:

```json
{
  "guild": {
    "id": "123456789012345678"
  },
  "players": [
    {
      "id": "234567890123456789",
      "xp": 1234,
      "message_count": 56,
      "username": "Example member"
    }
  ]
}
```

Guild and player IDs must be JSON strings. Import is currently JSON-only and is
available through the dashboard or `POST /api/guilds/{guildId}/xp/import/mee6`;
Rankoon does not fetch data directly from MEE6. Re-importing replaces the
imported portion instead of adding it again.

## Requirements

### Self-hosting

- Docker with Linux container support, or the local development toolchain below
- A reachable MongoDB instance
- A Discord application with a bot user
- A public HTTP(S) origin for production dashboard OAuth callbacks

### Local development

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 22](https://nodejs.org/) and npm
- MongoDB reachable at the configured connection string
- A Chromium-based browser for the default Karma frontend test runner

The repository does not pin a .NET SDK patch version. Node.js 22 is the version
used by the Docker build and GitHub Actions.

## Discord Application Setup

1. Create an application in the
   [Discord Developer Portal](https://discord.com/developers/applications) and
   add a bot user.
2. Enable **Server Members Intent** and **Message Content Intent** on the Bot
   page. Rankoon also requests guilds, voice states, guild messages, message
   reactions, and scheduled event intents; it does not request presence data.
3. Add the OAuth redirect URI. For local development, use
   `http://localhost:5020/api/auth/callback`. For production, use the exact
   public callback configured as `DISCORD_REDIRECT_URI`.
4. Put the application ID, client secret, and bot token in the backend's
   environment configuration. Never commit these values.
5. Start Rankoon, sign in to the dashboard, select a server, and use the install
   action generated by the backend for that Discord application.

Rankoon's generated bot invitation requests the `bot` and
`applications.commands` scopes with these permissions:

| Permission | Used for |
| --- | --- |
| View Channels | Read configured channels and receive relevant activity |
| Send Messages | Permission included by the configured bot invite |
| Manage Channels | Create, update, and delete hub and temporary channels |
| Move Members | Move members from hubs and support voice owner controls |
| Manage Roles | Synchronize level reward roles and assign self roles |
| Embed Links | Publish self-role panel embeds |
| Add Reactions | Seed the configured self-role reactions |
| Read Message History | Reconcile self-role panel messages after reconnects |
| Manage Messages | Update panel reactions and delete published panels |

Rankoon does not request Administrator. Discord category overrides can still
deny a permission, and Manage Roles works only for roles below the bot's highest
role.

## Run with Docker

The multi-stage [`Backend/Dockerfile`](Backend/Dockerfile) builds the Angular
frontend and ASP.NET Core backend, serves both from one container, and listens on
port `8080`. MongoDB is not included.

There is currently no Compose file, so first make sure the configured MongoDB
host is reachable from the Rankoon container.

1. Create the environment file:

   ```sh
   cp deploy/.env.example .env
   ```

2. Replace every example value in `.env`:

   ```dotenv
   MONGODB_CONNECTION_STRING=mongodb://your-mongodb-host:27017
   MONGODB_DATABASE_NAME=rankoon
   DISCORD_CLIENT_ID=your_discord_application_client_id
   DISCORD_CLIENT_SECRET=your_discord_application_client_secret
   DISCORD_BOT_TOKEN=your_discord_bot_token
   JWT_SECRET_KEY=replace_with_a_long_random_secret_key
   DISCORD_REDIRECT_URI=https://rankoon.example.com/api/auth/callback
   FRONTEND_BASE_URL=https://rankoon.example.com
   ```

3. Build from the repository root and start the container:

   ```sh
   docker build -f Backend/Dockerfile -t rankoon:local .
   docker run -d \
     --name rankoon \
     --restart unless-stopped \
     --env-file .env \
     -p 8080:8080 \
     rankoon:local
   ```

Use a reverse proxy for TLS in a public deployment, and configure both public
URL values with the same external origin. The frontend and API are intentionally
served together because the backend does not configure CORS for a separately
hosted frontend.

The release workflow is configured to publish images to
`ghcr.io/rankoon-bot/rankoon`. Package visibility is controlled in GitHub's
package settings and cannot be established from this repository alone. If the
package is public, replace the local image name above with a published version
tag such as `ghcr.io/rankoon-bot/rankoon:<version>`.

The unauthenticated `GET /api/version` endpoint reports the running assembly
version. It is not a liveness or readiness check.

## Configuration

Rankoon loads `.env`, standard ASP.NET Core configuration, and environment
variables. The checked-in settings use the flat variables below; nested settings
can also be overridden with ASP.NET Core's double-underscore syntax, such as
`MongoDb__ConnectionString`.

| Variable | Required | Purpose |
| --- | --- | --- |
| `MONGODB_CONNECTION_STRING` | Yes | MongoDB connection string |
| `MONGODB_DATABASE_NAME` | Yes | MongoDB database name |
| `DISCORD_CLIENT_ID` | Yes | Discord application ID used by OAuth, installation, and command registration |
| `DISCORD_CLIENT_SECRET` | Yes | Discord OAuth client secret |
| `DISCORD_BOT_TOKEN` | Yes | Discord bot token |
| `JWT_SECRET_KEY` | Yes | Signing key for Rankoon access tokens; use a long random value |
| `DISCORD_REDIRECT_URI` | Production | Exact Discord OAuth callback URL |
| `FRONTEND_BASE_URL` | Yes | Browser-visible Rankoon origin used after OAuth |

Selected optional overrides include:

| Variable | Default | Purpose |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://+:8080` in Docker | HTTP listener inside the container |
| `Jwt__Issuer` | `Rankoon` | JWT issuer |
| `Jwt__Audience` | `RankoonUsers` | JWT audience |
| `Jwt__AccessTokenExpirationMinutes` | `60` | Access-token lifetime |
| `Jwt__RefreshTokenExpirationDays` | `30` | Refresh-token lifetime |
| `RateLimiting__LeaderboardPermitLimit` | `90` | Public leaderboard requests per minute and partition |
| `RateLimiting__ReportsPermitLimit` | `60` | Report requests per minute and partition |
| `RateLimiting__QueueLimit` | `2` | Queued requests per rate-limit partition |
| `Serilog__MinimumLevel__Default` | `Information` | Default log level |

See [`Backend/appsettings.json`](Backend/appsettings.json),
[`Backend/.env.example`](Backend/.env.example), and
[`deploy/.env.example`](deploy/.env.example) for the authoritative checked-in
configuration. Do not place the Discord client secret or bot token in frontend
configuration.

## Local Development

### 1. Configure MongoDB and Discord

Copy the backend example and fill in the Discord credentials and JWT key:

```sh
cp Backend/.env.example Backend/.env
```

The development settings already define:

- API: `http://localhost:5020`
- Dashboard: `http://localhost:4200`
- OAuth callback: `http://localhost:5020/api/auth/callback`
- MongoDB default: `mongodb://localhost:27017`

Run the backend from `Backend` so DotNetEnv finds `Backend/.env`.

### 2. Start the backend

```sh
cd Backend
dotnet restore Rankoon.sln
dotnet run --project Rankoon.csproj
```

MongoDB indexes and small compatibility migrations are initialized by a hosted
service at startup. There is no separate relational migration step. Existing
member documents missing leaderboard fields receive compatible defaults and
recalculated totals; legacy voice holdback settings are removed.

### 3. Start the dashboard

In a second terminal:

```sh
cd Frontend
npm ci
npm start
```

Open `http://localhost:4200`. Angular's development proxy forwards `/api` to
`http://localhost:5020`, matching the production same-origin layout.

## Testing and Building

Run backend tests and a release build from `Backend`:

```sh
dotnet test Rankoon.sln
dotnet build Rankoon.sln --configuration Release
```

Run frontend tests and the production build from `Frontend`:

```sh
npm test
npm run build
```

The backend xUnit suite covers API contracts plus XP projection, season scheduling,
and voice watchdog behavior. The frontend uses Jasmine and Karma. On pushes to
`main`, the release workflow runs `Build and test` once with:

```sh
# Backend, in Backend/
dotnet restore Rankoon.sln
dotnet build Rankoon.sln --configuration Release --no-restore
dotnet test Rankoon.sln --configuration Release --no-build --no-restore

# Frontend, in Frontend/
npm ci
npm test -- --watch=false --browsers=ChromeHeadless
npm run build
```

`Release and publish container` runs only on `main`, creates a semantic release,
and builds and publishes the container only after the validation succeeds.

## Architecture

```text
Discord Gateway/API
       |
       v
+----------------------------+
| ASP.NET Core 9             |
| - Discord.Net bot          |
| - OAuth/JWT API            |
| - XP and voice workers     |
| - Angular static hosting   |
+-------------+--------------+
              |
              v
          MongoDB
```

Production uses one application process for the API, Discord gateway client,
background workers, and dashboard. MongoDB stores users and refresh tokens, XP
settings and ledgers, member totals, leaderboards, voice sessions and hubs,
temporary-channel ownership, role policies, guild statistics, and reports.

Key technologies:

- ASP.NET Core and .NET 9
- Discord.Net 3.18
- MongoDB.Driver 3.4
- Angular 20 with Transloco
- Serilog console logging
- xUnit, Jasmine, and Karma

## Repository Layout

```text
.
|-- Backend/                 ASP.NET Core API, Discord bot, workers, and tests
|   |-- Backend.Tests/       xUnit API tests
|   |-- Controllers/         OAuth, guild, leaderboard, and reporting endpoints
|   |-- Data/                Auth, Discord, XP, MongoDB, reporting, and models
|   |-- Dockerfile           Combined frontend/backend production image
|   `-- Rankoon.sln          Backend and test solution
|-- Frontend/                Angular dashboard and public leaderboard
|-- deploy/.env.example      Production environment template
|-- .github/workflows/       Commit validation and release/container automation
|-- CHANGELOG.md             Generated release history
|-- DEPLOYMENT.md            Additional container deployment notes
`-- LICENSE                  GNU Affero General Public License v3.0
```

## Contributing

Contributions should stay aligned with Rankoon's focused scope: leveling, voice
activity, rankings, and community voice management.

1. Open an issue or review existing issues before starting a large behavioral
   change.
2. Create a focused branch and keep unrelated changes separate.
3. Use [Conventional Commits](https://www.conventionalcommits.org/) so semantic
   release can determine the next release version.
4. Run the relevant backend and frontend tests and builds documented above.
5. Open a pull request describing the user-visible behavior, configuration or
   persistence impact, and verification performed.

A dedicated `CONTRIBUTING.md` and pull-request template are not yet present, so
this section describes the workflow enforced by the current repository.

## Current Limitations

- No Docker Compose file or built-in MongoDB service is provided.
- No dedicated liveness/readiness endpoint or Docker `HEALTHCHECK` is implemented.
- The Discord client currently runs with one shard, and OAuth state is stored in
  process memory; horizontal multi-instance deployment is not documented.
- The configured level-up channel is stored in settings, but level-up
  notifications are not currently sent.
- Slash commands are global, guild-only in their handlers, and currently return
  English output except for `/voice`, whose responses are German.
- There is no OpenAPI/Swagger UI or external metrics/tracing integration.
- OAuth callback tokens are returned in the frontend callback URL query string.
  This is an implemented limitation; the frontend should consume and remove them
  promptly, and deployments should avoid logging full URLs.

## Security

Keep Discord credentials, JWT signing keys, and MongoDB credentials out of Git.
Use secret environment variables in production and rotate any value that may have
been exposed.

The repository does not currently contain a `SECURITY.md` or a documented private
vulnerability-reporting channel. Do not infer a security-reporting address from
the project name. Until a private channel is published, avoid placing credentials
or exploit details in a public GitHub issue.

## Releases

Releases are generated from Conventional Commits with semantic-release. The
release workflow updates [`CHANGELOG.md`](CHANGELOG.md), creates GitHub release
metadata, and builds a versioned container for a newly released `main` revision.
Non-main branch pushes are also configured to publish branch and commit-SHA image
tags.

## License

Rankoon is free and open source under the
[GNU Affero General Public License v3.0](LICENSE).
