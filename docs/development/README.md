# Development

Follow the [engineering principles](contributing.md) for small, reusable modules
and behavior-preserving refactoring. Commands below start at the repository root
unless a working directory is specified.

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

## Local Development

### 1. Configure MongoDB and Discord

Follow the [Discord setup](../operations/discord-setup.md), then copy the backend
example and fill in MongoDB, Discord credentials, and the JWT key:

```sh
cp Backend/.env.example Backend/.env
```

The development configuration and environment example use:

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

Open `http://localhost:4200`. Angular's development proxy forwards `/api` and
`/hubs`, including WebSockets, to `http://localhost:5020`, matching the production
same-origin layout.

### Local leaderboard demo

In Development, server owners can use the dashboard's `DEV` section to generate
up to 500 mock members, trigger XP events through the regular XP and SignalR
paths, and remove the mock data. The backend endpoints return `404` outside
Development. Use a disposable local database for this workflow.

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
and voice watchdog behavior. The frontend uses Jasmine and Karma. Set
`RANKOON_SEASON_TEST_MONGO` to an isolated test MongoDB to include database
integration tests; without it, the optional MongoDB tests are skipped. CI provides
MongoDB and sets this variable.

On pushes to `main` and `dev`, the release workflow runs `Build and test` with:

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

`Release and publish container` publishes validated images for `main` and `dev`.
Semantic releases are created from `main`. See the
[deployment guide](../operations/deployment.md#github-container-registry) for tags
and artifact packaging.

For changes requiring Discord or browser interaction, use the
[manual acceptance plan](../testing/manual-test-plan.md) and the relevant focused
checklist in the [documentation index](../README.md#verification).
