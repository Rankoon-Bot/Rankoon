# Configuration

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
| `DATAPROTECTION__KEYRINGPATH` | Required outside Development | Persistent, non-image directory for ASP.NET Core Data Protection keys; shared by replicas of one deployment |
| `Jwt__Issuer` | `Rankoon` | JWT issuer |
| `Jwt__Audience` | `RankoonUsers` | JWT audience |
| `Jwt__AccessTokenExpirationMinutes` | `15` | Access-token lifetime |
| `Jwt__RefreshTokenExpirationDays` | `30` | Refresh-token lifetime |
| `RateLimiting__Leaderboard__PermitLimit` | `90` | Public leaderboard requests per minute and partition |
| `RateLimiting__Reports__PermitLimit` | `60` | Report requests per minute and partition |
| `RateLimiting__Leaderboard__QueueLimit` | `2` | Queued leaderboard requests per rate-limit partition; other policies have their own `QueueLimit` |
| `Serilog__MinimumLevel__Default` | `Information` | Default log level |
| `VoiceWatchdog__ReconciliationIntervalSeconds` | `60` | Global Discord voice reconciliation interval; must be 10-600 seconds |
| `VoiceActivity__CheckpointIntervalSeconds` | `30` | Voice activity checkpoint interval |
| `VoiceActivity__ProjectionIntervalSeconds` | `30` | Persisted voice projection cadence; must be 15-30 seconds |
| `VoiceActivity__MaximumSegmentsPerDocument` | `2000` | Daily document rollover threshold |
| `VoiceActivity__ProjectionBatchSize` | `100` | Projection repair batch size |
| `VoiceActivity__MigrationBatchSize` | `500` | Default legacy voice migration batch size |

See [`Backend/appsettings.json`](../../Backend/appsettings.json),
[`Backend/.env.example`](../../Backend/.env.example), and
[`deploy/.env.example`](../../deploy/.env.example) for the authoritative checked-in
configuration. Do not place the Discord client secret or bot token in frontend
configuration.

Custom bot identity also needs its separate fingerprint key and explicit enablement;
see the [feature reference](../features/overview.md#custom-bot-identity) and
[key-ring operations](deployment.md#data-protection-key-ring). For policy-specific
limits and reverse-proxy configuration, see [rate limiting](../backend/rate-limiting.md).
