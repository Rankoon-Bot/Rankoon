# Rankoon product principles

- Design every feature for users with no prior experience. Use plain language, useful defaults, a clear primary next step and contextual examples.
- Make the current state and the consequences of each action visible before execution. Distinguish drafts, saved settings and running operations.
- Prevent invalid actions in both the UI and API. Protect against concurrent requests, repeated submissions and interrupted background work.
- Avoid surprising automation. Automatic creation must be explicitly enabled, bounded and easy to stop; cancellation must not silently recreate cancelled work.
- Explain errors with a concrete recovery step. Preserve user input and make retries safe.
- Verify important state transitions and recovery paths, as well as responsive and keyboard accessible interfaces.
- Follow `Frontend/AGENTS.md` and `Frontend/THEMING.md` for frontend work.
