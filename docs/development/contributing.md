# Contributing

Keep contributions aligned with Rankoon's scope: leveling, voice activity,
rankings, and community voice management. These principles apply to every change,
whether written manually or with development assistance.

## Small modules and reuse

Avoid monolithic files. Each file should have a clear responsibility that a human
or an automated tool can understand without loading unrelated features.

- Split independent responsibilities into focused modules. A page or controller
  should coordinate a use case; move substantial business rules, persistence,
  parsing, and presentation into appropriately named collaborators.
- Reuse existing components, services, models, helpers, and design tokens before
  adding another implementation of the same behavior.
- Keep feature-specific files together. Promote a module to a shared location when
  it has a concrete use across features; give it an explicit, narrow interface.
- Name files after their purpose. Avoid catch-all `utils`, `helpers`, or `common`
  files that collect unrelated behavior.
- Keep dependencies explicit and follow existing boundaries. Shared code must not
  depend on page-specific orchestration; controllers and views should not become
  the only place where domain rules can be reused.
- When extending a large file, extract the affected cohesive responsibility where
  practical. Do not keep appending unrelated behavior. Avoid speculative layers,
  empty wrappers, and splitting files merely to satisfy an arbitrary line count.
- Keep modules easy to locate, test, and review. Explain non-obvious invariants
  close to their implementation and put longer guides under `docs/`.

The goal is maintainability and a clear project structure for people and tools.
File size is a warning signal; responsibility and coupling determine the split.

## Behavior-preserving refactoring

Structural cleanup must preserve observable behavior unless the task explicitly
requests a behavior change. Before moving or extracting code, identify and retain:

- API routes, request and response shapes, status codes, and error contracts;
- authorization, validation, defaults, and configuration keys;
- database fields, indexes, migration behavior, and transaction boundaries;
- event ordering, retry keys, concurrency protection, cancellation, and recovery;
- user-visible flows, translations, accessibility, and build entry points.

Update imports, registrations, links, and tests with each move. Use existing
regression tests for the affected paths and add meaningful coverage when a
contract or recovery path lacks protection. Documentation-only moves require
link and reference checks; a build alone does not prove runtime equivalence.

## Product quality

Use plain language, useful defaults, contextual examples, and a clear next step.
Show current state and consequences before execution, including whether work is
a draft, saved, or running. Validate actions in both UI and API, preserve input on
errors, and provide a concrete recovery step. Automatic creation must be enabled
explicitly, bounded, easy to stop, and safe after cancellation or interruption.

## Contribution workflow

1. Discuss large behavior changes in an issue before implementation.
2. Work in a focused branch and keep unrelated changes separate.
3. Use Conventional Commits so the existing release tooling can determine versions.
4. Run the relevant [tests and builds](README.md#testing-and-building). Check state
   transitions, retries, concurrency, and recovery where the change affects them.
   For frontend work, follow the [theming guide](theming.md).
5. Update the relevant guide and the [documentation index](../README.md) if needed.
6. Describe the resulting behavior, configuration or persistence impact, and
   actual verification in the pull request.

Do not put credentials in source, fixtures, screenshots, or public reports.
Keep the main README focused on the product and entry points. Detailed working
agreements for development tools belong in [docs/agents/](../agents/README.md).
