# Rankoon

**Free, open-source leveling for communities that actually talk.**

Rankoon is a configurable Discord bot that rewards meaningful text and voice
activity, provides server rankings, and creates member-managed temporary voice
channels. An Angular dashboard brings configuration and reporting together.

[![License: AGPL v3](https://img.shields.io/github/license/Rankoon-Bot/Rankoon)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Rankoon-Bot/Rankoon)](https://github.com/Rankoon-Bot/Rankoon/releases/latest)
[![Release and publish container](https://github.com/Rankoon-Bot/Rankoon/actions/workflows/release.yml/badge.svg)](https://github.com/Rankoon-Bot/Rankoon/actions/workflows/release.yml)

## Features

- **Activity XP:** configurable rewards for messages, reactions, threads, events,
  and eligible voice participation.
- **Progress and rankings:** MEE6-compatible levels, role rewards, seasons,
  XP history, and public or members-only leaderboards.
- **XP imports:** bring existing MEE6 or legacy Rankoon data without discarding
  activity already earned in Rankoon.
- **Voice hubs:** temporary channels with member-managed names, limits, and ownership.
- **Administration:** dashboard permissions, self-role panels, bot diagnostics,
  configurable level-up announcements, and activity reports.
- **Self-hosting:** one Linux application container for the dashboard, API, Discord
  client, and workers, backed by MongoDB.

See the [feature reference](docs/features/overview.md) for behavior and commands,
[season planning](docs/features/seasons.md) for lifecycle and recovery, and
[XP imports](docs/features/xp-imports.md) for supported formats.

## Getting started

Rankoon is under active development. The core implementation is free and open
source, with no subscription-gated functionality. A public hosted bot invitation
is not currently documented in this repository.

For your own instance:

1. Create a [Discord application](docs/operations/discord-setup.md).
2. Configure MongoDB and the required [environment values](docs/operations/configuration.md).
3. Follow the [container deployment guide](docs/operations/deployment.md), including
   the persistent Data Protection key ring.

For local development, start with the [development guide](docs/development/README.md).
It covers .NET 9, Node.js 22, MongoDB, startup, tests, and production builds.

## Repository

```text
Backend/     ASP.NET Core API, Discord integration, workers, and backend tests
Frontend/    Angular dashboard, shared UI, and frontend tests
docs/        Product, development, operations, and verification guides
deploy/      Production environment template
.github/     Build, test, release, and container workflows
```

The [documentation index](docs/README.md) links to the detailed guides. The
[architecture map](docs/development/architecture.md) explains the source layout.

## Contributing

Follow the [contribution and engineering principles](docs/development/contributing.md).
Keep modules focused, reuse existing building blocks, and preserve behavior when
refactoring. Assisted contributions follow the same review and verification standards;
repository tooling should read the [working agreement](docs/agents/README.md) first.

Use Conventional Commits. The release workflow validates `main` and `dev`, publishes
container images, and uses semantic-release for releases from `main`. See
[deployment](docs/operations/deployment.md#github-container-registry) and the
[generated changelog](CHANGELOG.md).

## Security and deployment limits

Keep Discord credentials, JWT signing keys, MongoDB credentials, and key-ring
material out of Git. Use HTTPS for public deployments and keep the dashboard and
API on the same origin. The [authentication guide](docs/backend/authentication.md)
describes cookie sessions and CSRF protection.

MongoDB is operated separately; no Docker Compose file is included. OAuth state
and rate limits are process-local, so multiple active instances need deployment
planning. Read the [authentication flow](docs/backend/authentication-flow.md) and
[proxy and rate-limit guidance](docs/backend/rate-limiting.md).

A private vulnerability-reporting channel is not currently documented. Avoid
posting credentials or exploit details in public issues.

## License

[GNU Affero General Public License v3.0](LICENSE).
