# Authentication Flow

1. The browser requests `GET /api/auth/login?returnUrl=/dashboard`.
2. Rankoon validates the optional route, creates an opaque OAuth state, and keeps
   both in its in-memory cache for five minutes.
3. The browser follows the returned Discord authorization URL.
4. Discord calls `GET /api/auth/callback` with an authorization code and state.
5. Rankoon verifies and consumes state, exchanges the code, reads the Discord user,
   and creates or updates the stored Discord user and OAuth tokens.
6. Rankoon issues a JWT access token and a hashed, family-associated refresh token,
   then redirects to the frontend callback with `token`, `refresh_token`,
   `expires_at`, and an accepted `return_url` in the query.
7. The frontend sends `Authorization: Bearer <token>` and replaces both values
   after `POST /api/auth/refresh`.

## Refresh And Logout

1. The client posts its refresh token to `/api/auth/refresh`.
2. Rankoon hashes it, supports a legacy plaintext record, and atomically marks the
   matching unexpired token revoked as `Rotated`.
3. It creates a replacement hashed token in the same family and returns it with a
   new access token.
4. A second use of a consumed token marks replay and revokes the whole family.
5. `POST /api/auth/logout` revokes every unrevoked token in the token's family.

## Security Boundaries

- OAuth state and return routes are process-local, so login and callback must reach
  the same application instance.
- Return routes are restricted to safe application-relative paths.
- New refresh-token plaintext values are never stored in MongoDB; legacy records
  remain supported during migration.
- Callback query tokens are an existing limitation. Consume and remove them
  promptly, and do not retain callback URLs in logs or telemetry.
