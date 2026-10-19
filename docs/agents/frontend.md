# Frontend working agreement

The repository-wide [working agreement](README.md) also applies here.
The [theming guide](../development/theming.md) is binding for every frontend
change; read it before working on Angular templates, SCSS, or visual assets.

- Use the central tokens in
  [`Frontend/src/styles/_tokens.scss`](../../Frontend/src/styles/_tokens.scss).
  Do not introduce local design values.
- Extend existing patterns before inventing variants. Reuse the controls in
  `Frontend/src/app/shared/ui/` and existing components when they fit.
- Keep the interface dark-first and use Rankoon red sparingly as a signal accent.
- Keep page orchestration, reusable UI, state, API access, and pure transformation
  logic in focused files. Follow the shared
  [module principles](../development/contributing.md#small-modules-and-reuse).
- For UI changes, run at least `npm run build` from `Frontend/`. Check affected
  views on desktop and mobile and verify keyboard access and visible focus.
- Run relevant frontend tests when changing behavior and preserve translations.
