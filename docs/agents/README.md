# Working agreement

These instructions apply throughout the repository. Read the shared
[contribution and engineering principles](../development/contributing.md) before
editing. They are binding for assisted work as well as human contributions.

## Before changing files

1. Check the current diff and preserve unrelated work.
2. Read the [architecture map](../development/architecture.md) and the relevant
   guide in the [documentation index](../README.md).
3. For frontend work, also follow [frontend guidance](frontend.md) and the
   [theming guide](../development/theming.md) before changing Angular templates,
   SCSS, or visual assets.

## Product principles

- Design every feature for users with no prior experience. Use plain language,
  useful defaults, a clear primary next step, and contextual examples.
- Make the current state and the consequences of each action visible before
  execution. Distinguish drafts, saved settings, and running operations.
- Prevent invalid actions in both the UI and API. Protect against concurrent
  requests, repeated submissions, and interrupted background work.
- Avoid surprising automation. Automatic creation must be explicitly enabled,
  bounded, and easy to stop; cancellation must not silently recreate cancelled work.
- Explain errors with a concrete recovery step. Preserve user input and make
  retries safe.
- Verify important state transitions and recovery paths, as well as responsive
  and keyboard accessible interfaces.

## Implementation and handoff

- Avoid monolithic files. Keep responsibilities small and reuse existing modules;
  apply the extraction rules in the shared engineering principles.
- Keep structural refactoring separate from intentional behavior changes. Preserve
  public contracts, defaults, validation, authorization, and persistence semantics.
- Run the relevant [checks](../development/README.md#testing-and-building).
  Distinguish successful tests from builds, skipped tests, and manual checks.
- Update the authoritative guide when behavior, setup, or structure changes.
  Keep task-specific notes out of source directories and the main README.
- In the handoff, state what changed, what was verified, and any remaining limits.

Keep future agent instructions in this directory. Root and scoped `AGENTS.md`
files should remain short links to these agreements rather than duplicate rules.
