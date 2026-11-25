# Routine video library
Implement the approved backend in the current Static-X worktree; preserve all unrelated work and the index. No live S3, database updates, commits or publishing. Verification uses the existing xUnit/VSTest solution.

## Specs

- S1 — “dentro de rutinas quiero que me hagas una seccion de videoteca, donde unicamente en esta seccion se suban videos informarivos o sobre ejercicios para los alumnos”. Follow the [approved design](../../Docs/superpowers/specs/2026-11-23-routine-video-library-design.md) for private S3 uploads, gym-scoped listing, and coverage-gated playback.
- S2 — “me sirve, unicamente agrega tambien que se puedan eliminar y reemplazar los videos”. Apply the approved design's revision-checked replacement, deletion, and durable cleanup rules.

## Tasks

- [ ] T1 — S1–S2: deliver the five API operations with EF persistence, private object storage, cleanup, migration preparation, and isolated tests; delegated direct (long-session context). No commit authorized.

## Log

- L1 — “dentro de rutinas quiero que me hagas una seccion de videoteca, donde unicamente en esta seccion se suban videos informarivos o sobre ejercicios para los alumnos”.
- L2 — “me sirve, unicamente agrega tambien que se puedan eliminar y reemplazar los videos”.
- L3 — “vamos con ello”. Risk: items 1, 2 and 4 (object deletion, tenant authorization, and catalog/object consistency under concurrent requests). Engram mirror pending: workspace identity not verified.
