# Student routines
Implement the approved basic backend on the current `master` worktree without commits, staging changes, or database updates. Verification uses the existing Static-X test project.

## Specs

- S1 — “trabajemos sin borradores unicamente con las fichas originales”. Implement the direct publication and existing-profile rules in [the approved design](../../Docs/superpowers/specs/2026-11-18-routines-design.md).
- S2 — “vayamos con este modulo basico inicial”. Deliver the three proposed HTTP endpoints with the approved permissions, coverage enforcement, atomic replacement and revision checks; verify against existing conventions.
- S3 — “hagamos eso”. Add the approved optional routine title, day names, and general and per-day instructions to the existing routine endpoints. Preserve publication behavior and compatibility with requests that omit these fields.
- S4 — “hagamos eso”. Accept exactly one positive exercise target: existing `repetitions` or new `durationSeconds`. Keep `restSeconds` separate, preserve existing repetition-based routines and requests, and add no routes.

## Tasks

- [x] T1 — S1–S2: persistence, API, tests, and migration preparation; delegated direct. RED: 11 missing-route failures. GREEN: 787 solution tests passed, including 16 routine tests independently verified. No commit authorized.
- [x] T2 — S3: optional routine metadata; delegated direct, recovered after interruption. Build and 809 solution tests passed; 25 routine tests independently passed. RED evidence unavailable after interruption. No commit authorized.
- [x] T3 — S4: exercise targets and schema migration; delegated direct (long-session context). RED: 3 failures; GREEN: 831 solution tests, including 47 routine tests independently verified. No commits or database updates authorized.

## Log

- L1 — “vayamos con este modulo basico inicial”.
- L2 — Selected “Sobre los cambios actuales”. Preserve existing work and the index; never apply migrations.
- L3 — Risk: item 2 (tenant authorization and live coverage), item 4 (concurrent publication). Independent HTTP and SQLite verification passed. The additive SQL Server migration was checked offline and not applied.
- L4 — No isolated commit or PR slice exists for native review. The worktree combines existing changes with the implementation; an assessment of the mixed worktree refused the undeclared untracked inventory. No review receipt was obtained.
- L5 — “continua, se interrumpio la tarea”. Recovered the existing implementation rather than reapplying it. Risk: item 1 (schema migration). `20261123000000_AddRoutineMetadata` adds four nullable columns; offline checks passed. Migration not applied; native review assessment could not resolve the mixed untracked inventory.
- L6 — “hagamos eso”. Risk: item 1 (existing exercise records and schema migration). Preserve stored repetitions; do not invent values on rollback.
- L7 — `20261123010000_AddExerciseDuration` passed offline checks; live SQL Server execution was not attempted. The normal Debug build was blocked by the running API; a separate output path built without warnings or errors and ran the full suite. Native assessment refused the undeclared mixed untracked inventory; no formal review receipt.
- Engram mirror pending: workspace identity not verified in this runtime.
