# Architecture

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

## Source map

| Path | Responsibility |
| --- | --- |
| [`Backend/Controllers/`](../../Backend/Controllers/) | HTTP endpoints, request validation, and use-case coordination |
| [`Backend/Api/`](../../Backend/Api/) | API errors, routing conventions, and rate-limit configuration |
| [`Backend/Data/Auth/`](../../Backend/Data/Auth/) | Authentication, browser sessions, and authorization |
| [`Backend/Data/Discord/`](../../Backend/Data/Discord/) | Discord runtime, commands, activity ingress, and voice hubs |
| [`Backend/Data/Xp/`](../../Backend/Data/Xp/) | XP rules, projections, imports, seasons, and recovery |
| [`Backend/Data/MongoDb/`](../../Backend/Data/MongoDb/) | Database access, indexes, and startup maintenance |
| [`Backend/Data/Model/`](../../Backend/Data/Model/) | Persisted models and domain contracts |
| [`Backend/Data/Operations/`](../../Backend/Data/Operations/), [`Reporting/`](../../Backend/Data/Reporting/), [`Performance/`](../../Backend/Data/Performance/) | Operational history, reporting, and instrumentation |
| [`Backend/Hubs/`](../../Backend/Hubs/) | SignalR endpoints and subscriptions |
| [`Backend/Backend.Tests/`](../../Backend/Backend.Tests/) | Backend unit, contract, and persistence tests |
| [`Frontend/src/app/`](../../Frontend/src/app/) | Dashboard features, shared UI, services, and state; see the [frontend source map](../../Frontend/README.md#source-layout) |
| [`Frontend/src/styles/`](../../Frontend/src/styles/) | Design tokens and shared styles |
| [`deploy/`](../../deploy/) | Production environment template |
| [`.github/workflows/`](../../.github/workflows/) | Validation, artifact packaging, and releases |

## Module boundaries

Follow the [small-module and reuse rules](contributing.md#small-modules-and-reuse).
Keep feature rules with their feature, presentation with UI, and persistence with
data access. Extract a coherent responsibility when a file starts coordinating
several independent concerns. Share established behavior through explicit
interfaces rather than copying it into each caller.

Documentation lives under [`docs/`](../README.md). The main README introduces the
product, component READMEs provide local entry points, and generated release
history remains in [`CHANGELOG.md`](../../CHANGELOG.md).
