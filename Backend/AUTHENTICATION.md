# Authentication

Rankoon uses Discord OAuth2 for sign-in and Rankoon-issued JWT access and refresh
tokens for its API. Discord user tokens are retained with the user record for
Discord API calls such as listing guilds.

## Endpoints

| Endpoint | Authentication | Behavior |
| --- | --- | --- |
| `GET /api/auth/login?returnUrl=/path` | No | Returns a Discord OAuth `loginUrl`. |
| `GET /api/auth/callback?code=...&state=...` | Discord | Exchanges the code and redirects to the frontend callback. |
| `POST /api/auth/refresh` | No | Accepts `{ "refreshToken": "..." }` and returns a rotated token response. |
| `POST /api/auth/logout` | No | Accepts `{ "refreshToken": "..." }` and revokes its token family. |
| `GET /api/auth/me` | Bearer JWT | Returns the current Rankoon user. |
| `GET /api/auth/validate` | Bearer JWT | Returns the supplied access token, expiry, and user. |
| `GET /api/auth/guilds` | Bearer JWT | Returns accessible Discord guilds and bot-install URLs. |

There are no `/api/auth/verify` or `/api/auth/test` endpoints.

## OAuth And Return Routes

`login` creates opaque OAuth state and retains state plus an accepted return route
in process memory for five minutes. The callback consumes the state. This store is
not shared across application instances.

Only an application-relative return route is accepted: it starts with one slash,
does not start with `//`, contains no backslash, and parses as a relative URI.
External and protocol-relative URLs are discarded. A successful callback includes
the accepted route as `return_url`.

The callback redirects to `{FRONTEND_BASE_URL}{Frontend:CallbackPath}` with
URL-escaped `token`, `refresh_token`, and `expires_at` query parameters. This is a
known limitation: tokens can be exposed in browser history, referrers, or URL logs
until the frontend consumes and removes them. Use HTTPS and avoid logging full
callback URLs.

## Refresh Tokens

New refresh tokens are random opaque values. Only a SHA-256 hash is persisted for
new records; the legacy plaintext `token` field remains readable for migration
compatibility. Each login starts a token family.

Refreshing atomically consumes and revokes the presented token, then returns a
replacement access and refresh token in the same family. Reuse of a consumed token
is replay and revokes all unrevoked family tokens. Logout also revokes the family.
Records retain issued and last-used IP addresses when available. Defaults are 60
minutes for access tokens and 30 days for refresh tokens under `Jwt` settings.

## Configuration

Set `DISCORD_CLIENT_ID`, `DISCORD_CLIENT_SECRET`, `DISCORD_REDIRECT_URI`,
`JWT_SECRET_KEY`, `MONGODB_CONNECTION_STRING`, `MONGODB_DATABASE_NAME`, and
`FRONTEND_BASE_URL`. Register the exact redirect URI with Discord. Never expose
the Discord client secret, bot token, or JWT key to the frontend.
