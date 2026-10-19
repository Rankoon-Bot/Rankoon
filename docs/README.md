# Documentation

Start with the guide that matches your task. Commands use the repository root as
their working directory unless a section explicitly says otherwise.

## Getting started

- [Development setup and checks](development/README.md)
- [Discord application setup](operations/discord-setup.md)
- [Container deployment and key-ring operations](operations/deployment.md)
- [Configuration reference](operations/configuration.md)

## Product behavior

- [Feature reference and slash commands](features/overview.md)
- [Importing existing XP](features/xp-imports.md)
- [Season planning, cancellation, and recovery](features/seasons.md)

## Development

- [Contribution workflow and engineering principles](development/contributing.md)
- [Architecture and source layout](development/architecture.md)
- [Frontend design and accessibility](development/theming.md)

## Backend reference

- [Authentication contract](backend/authentication.md) and [request flow](backend/authentication-flow.md)
- [Rate limits and trusted proxies](backend/rate-limiting.md)
- [Performance instrumentation](backend/performance.md)
- [MongoDB scan classification](backend/scan-classification.md)
- [Legacy voice ledger migration](backend/voice-ledger-migration.md)
- [XP integrity and recovery](backend/xp-integrity.md)
- [XP settings migrations and backups](backend/xp-settings-migrations.md)

## Verification

- [Manual acceptance plan](testing/manual-test-plan.md)
- [Discord command acceptance checks](testing/discord-commands.md)
- [Bot installation history checks](testing/bot-guild-history.md)
- [XP ledger review, 19 October 2026](testing/xp-ledger-review-2026-10-19.md)
  records a historical investigation and its verification limits.

## Maintaining this documentation

Keep each document focused on one subject. Add new guides to this index and link
to the authoritative document instead of copying its rules into several places.
Keep release history in the generated [changelog](../CHANGELOG.md).

Automation-specific working agreements live in [agents/](agents/README.md).
The small `AGENTS.md` entry points in the source tree exist for tool discovery;
keep detailed instructions here under `docs/`.
