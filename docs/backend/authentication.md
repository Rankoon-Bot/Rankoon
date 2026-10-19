# Authentication

Rankoon uses Discord OAuth2 for sign-in and Rankoon-issued JWT access and refresh
tokens for its API. Browser credentials are issued as HttpOnly cookies; session
responses contain user state and expiry, without access or refresh tokens.
Discord user tokens are retained with the user record for Discord API calls such
as listing guilds.

## Endpoints

| Endpoint | Authentication | Behavior |
| --- | --- | --- |
| `GET /api/auth/login?returnUrl=/path` | No | Returns a Discord OAuth `loginUrl`. |
| `GET /api/auth/callback?code=...&state=...` | Discord | Exchanges the code and redirects to the frontend callback. |
| `GET /api/auth/csrf` | No | Bootstraps the antiforgery cookie and returns a request token. |
| `POST /api/auth/refresh` | Refresh cookie + CSRF | Rotates the cookie credentials and returns token-free session state. |
| `POST /api/auth/logout` | Refresh cookie when present + CSRF for browser sessions | Revokes the token family, clears cookies, and returns no content. |
| `GET /api/auth/me` | Access JWT | Returns the current Rankoon user. |
| `GET /api/auth/validate` | Access JWT | Returns session user and expiry without credentials. |
| `GET /api/auth/guilds` | Access JWT | Returns accessible Discord guilds and bot-install URLs. |

Browser JWT authentication reads the configured access cookie. An explicit
`Authorization: Bearer ...` header is also accepted. Refresh and logout read the
refresh credential from its cookie, not a JSON request body.

There are no `/api/auth/verify` or `/api/auth/test` endpoints.

## OAuth And Return Routes

`login` creates opaque OAuth state and retains state plus an accepted return route
in process memory for five minutes. The callback consumes the state. This store is
not shared across application instances.

Only an application-relative return route is accepted: it starts with one slash,
does not start with `//`, contains no backslash, and parses as a relative URI.
External and protocol-relative URLs are discarded. A successful callback includes
the accepted route as `return_url`.

The callback sets access and refresh cookies, then redirects to
`{FRONTEND_BASE_URL}{Frontend:CallbackPath}` with only the accepted `return_url`
when present. It does not place credentials in the callback URL. Use HTTPS in
production and serve the dashboard and API from the same public origin.

## Browser session protection

The checked-in production settings use `Secure`, `HttpOnly`, `SameSite=Strict`
cookies with `__Host-` names and path `/`. Development uses local cookie names
and allows HTTP. Angular keeps session state in memory and does not store access
or refresh credentials in browser storage.

For state-changing `/api` requests carrying an access or refresh cookie, the
server validates an antiforgery token. Obtain it from `GET /api/auth/csrf` and
send it in `X-CSRF-TOKEN`, or the configured `AuthCookies:CsrfHeaderName`.
Angular handles this through its HTTP interceptor. Missing or invalid tokens
return the canonical `auth.forbidden` error.

## Refresh Tokens

New refresh tokens are random opaque values. Only a SHA-256 hash is persisted for
new records; the legacy plaintext `token` field remains readable for migration
compatibility. Each login starts a token family.

Refreshing atomically consumes and revokes the presented token, then sets
replacement access and refresh cookies in the same family. Reuse of a consumed token
is replay and revokes all unrevoked family tokens. Logout also revokes the family.
Records retain issued and last-used IP addresses when available. Defaults are 15
minutes for access tokens and 30 days for refresh tokens under `Jwt` settings.

## Configuration

Set `DISCORD_CLIENT_ID`, `DISCORD_CLIENT_SECRET`, `DISCORD_REDIRECT_URI`,
`JWT_SECRET_KEY`, `MONGODB_CONNECTION_STRING`, `MONGODB_DATABASE_NAME`, and
`FRONTEND_BASE_URL`. Register the exact redirect URI with Discord. Never expose
the Discord client secret, bot token, or JWT key to the frontend. See the
[configuration reference](../operations/configuration.md) and
[step-by-step authentication flow](authentication-flow.md).
