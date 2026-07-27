# Rate Limiting

Rate limiting is configured in the `RateLimiting` section of `appsettings.json`. Every policy has a `PermitLimit`, `WindowSeconds`, and `QueueLimit`; costly guild mutations also have `ConcurrencyLimit`. Invalid values or proxy IP addresses fail startup validation.

| Policy | Endpoints | Default |
| --- | --- | --- |
| `leaderboard` | Rankings | 90/minute |
| `reports` | Guild reports | 60/minute |
| `bot-management` | Bot operations dashboard | 30/minute |
| `oauth-login`, `oauth-callback` | OAuth login and callback | 10/minute each |
| `oauth-refresh`, `oauth-logout` | Token refresh and logout | 12/minute each |
| `custom-bot-validation`, `custom-bot-save` | Custom bot validate and token save | 6/minute, 1 in flight per guild |
| `custom-bot-activate`, `custom-bot-restart` | Custom bot activate and restart | 3/minute, 1 in flight per guild |
| `xp-import` | Both XP import routes | 3/minute, 1 in flight per guild |
| `xp-settings` | XP configuration save | 10/minute, 1 in flight per guild |

Window partitions compose policy, authenticated Discord user when available, guild route value when available, and the normalized client IP. A `refresh_token` cookie, if one is supplied by a deployment, is represented only by a SHA-256 digest. The limiter does not read or emit raw refresh credentials. Rejections return the canonical `rateLimit.exceeded` API error, a `Retry-After` header, and `parameters.retryAfterSeconds`.

## Proxy Deployment

Set `RateLimiting:TrustedProxyIps` to the concrete IPs of reverse proxies that connect directly to Rankoon. Only these peers may supply `X-Forwarded-For` and `X-Forwarded-Proto`; one forwarding hop is accepted. Do not configure client networks, public ranges, or an empty catch-all proxy list. Without a trusted proxy configuration, forwarded headers are ignored and the direct peer address is limited.

## Multiple Instances

ASP.NET Core's built-in fixed-window and concurrency limiters are process-local. With multiple Rankoon instances, effective window capacity scales with the number of instances and per-guild concurrency can occur once per instance. Enforce shared edge limits at the load balancer/API gateway, or replace these policies with a distributed limiter before relying on limits as a cross-instance security boundary. Keep forwarding behavior identical on every instance.
