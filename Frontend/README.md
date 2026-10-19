# Rankoon dashboard

The Angular dashboard and public leaderboard for Rankoon. Shared setup,
architecture, and design documentation lives in [`docs/`](../docs/README.md).

## Run locally

Configure and start the backend using the [development guide](../docs/development/README.md).
Then run from this directory:

```sh
npm ci
npm start
```

Open `http://localhost:4200`. The development proxy forwards `/api` and `/hubs`
(including WebSocket traffic) to `http://localhost:5020`.

Discord sign-in runs through the backend callback at
`http://localhost:5020/api/auth/callback`. Browser sessions use HttpOnly cookies;
Discord secrets belong only in backend configuration. See the
[authentication contract](../docs/backend/authentication.md).

## Build and test

```sh
npm test -- --watch=false --browsers=ChromeHeadless
npm run build
```

## Source layout

| Directory | Responsibility |
| --- | --- |
| `src/app/pages/` | Feature pages and their local components |
| `src/app/components/` | Reusable application components |
| `src/app/shared/` | Shared UI controls, formatting, and charts |
| `src/app/layout/` | Application shell and navigation |
| `src/app/services/` | API access and application services |
| `src/app/store/` | Signal-based application and session state |
| `src/app/models/` | Shared API and view contracts |
| `src/app/guards/`, `src/app/interceptors/` | Route and HTTP boundaries |
| `src/app/i18n/`, `src/assets/i18n/` | Localization infrastructure and catalogs |
| `src/styles/` | Shared styles and semantic design tokens |
| `src/environments/` | Build environment configuration |

Follow the [module principles](../docs/development/contributing.md#small-modules-and-reuse)
and [theming guide](../docs/development/theming.md) when extending the dashboard.
Keep feature-specific code local and share reusable behavior through focused modules.

The project is licensed under [AGPL v3](../LICENSE).
