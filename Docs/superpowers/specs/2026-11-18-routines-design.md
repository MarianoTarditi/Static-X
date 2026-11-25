# Student routines: publish directly from the existing profile

Static-X will let trainers publish routines directly from existing student profiles. Each profile can have at most one published routine. No duplicate profile, persisted draft, or publication history is created.

## User flow

1. An enabled trainer opens an existing student profile in their gym and starts with an empty form or edits the current routine in the form. Closing the form without publishing does not save changes on the server.
2. The trainer organizes the routine into ordered days and orders the exercises within each day. Each exercise has a free-text name, positive sets, and exactly one target per set: positive integer `repetitions` or positive integer `durationSeconds`. Rest and notes are optional. `restSeconds` is separate from the target and may be zero. There is no shared exercise catalog.
3. To publish, the routine must have at least one day, and every day must have at least one exercise. Exercise names cannot be blank. Both targets together, neither target, and nonpositive targets are invalid. The unused target may be omitted or null. A successful publish creates the first routine or replaces the current one in a single operation. There is no separate action to unpublish the current routine in this release.
4. In **My routine**, a student with access sees the current routine for their explicitly linked profile. If no routine has been published, the app shows an empty state, not an authorization error. An access failure is not an empty state.

## Access rules

| Actor | Access |
| --- | --- |
| Enabled `Trainer` | Can read and publish the current routine for an existing profile in their own gym. No trainer-to-student assignment is required. |
| `Student` | Can read only the current routine for their explicitly linked profile, subject to the existing live student coverage rule. The self-service request does not select a student or gym. |
| Other roles | This release grants no routine access to other roles. Existing permissions for student profiles are unchanged. |

Derive identity, role, and gym from the authenticated session. Scope trainer requests to the authenticated gym and target profile, including writes and access to nested content. An out-of-gym target is indistinguishable from a missing target. Resolve the student's explicit account link on the server; do not infer it from contact email. Reuse existing authentication, CSRF protection for writes, and live coverage enforcement without a routine-specific exemption.

## Data and consistency

- A routine belongs to the existing student profile, not to the student's login. A routine contains ordered days, each with ordered exercises. Enforce at most one routine per profile in storage.
- Publication requires the revision returned by the trainer's read of the current routine. An explicit `null` expected revision means no routine existed when the form was opened; it is not permission to overwrite an existing routine. Each successful publication returns a new revision. A mismatch returns a conflict and prompts the trainer to reload.
- Validate the full routine before writing and verify the expected revision as part of the write. A failed replacement leaves the previous routine intact. Concurrent requests based on the same revision, including two first publications, cannot both succeed.
- Preserve legacy positive repetitions without converting or backfilling records. Requests that only supply repetitions keep working. Both read roles receive nullable `repetitions` and `durationSeconds` in saved order through the existing mapping.
- BLL and storage independently enforce exactly one positive target. The SQL CHECK uses explicit `IS NOT NULL` guards so a missing target cannot pass through SQL's UNKNOWN result.

### Prepared migration and rollback

`20261123010000_AddExerciseDuration` makes `Repetitions` nullable, adds nullable integer `DurationSeconds`, and replaces the exercise values CHECK. It changes no previous migration and introduces no defaults or data updates. The snapshot delegates to frozen migration metadata, following the existing convention.

SQL Server rollback first checks for timed rows or null repetitions under `TABLOCKX, HOLDLOCK` in the migration transaction. It throws before any schema changes if such rows exist. An authorized operator must explicitly republish those routines using repetitions or remove them before retrying; no repetitions are invented. With no timed rows, rollback restores non-null repetitions, drops duration, and restores the prior CHECK. SQL Server scripts and model parity are verified offline only; this migration has not been applied, and rollback has not been executed against SQL Server.

## HTTP endpoints

| Endpoint | Result |
| --- | --- |
| `GET /api/students/{studentId}/routine` | Trainer reads the current routine and its revision, or receives `200` with `{"routine": null}` for an existing profile with no publication. |
| `PUT /api/students/{studentId}/routine` | Trainer sends the full routine and expected revision. Returns `201` with the new routine's location on first publication or `200` on replacement, with the published routine and its new revision. |
| `GET /api/student/me/routine` | Student reads their own current routine or receives `200` with `{"routine": null}` when authorized but no routine has been published. |

## Backend responsibilities

| Layer | Responsibility |
| --- | --- |
| API | Expose trainer operations under the student profile and a separate student self-service read. Validate request shape, enforce the existing security middleware, and translate outcomes to HTTP responses. |
| BLL | Own the routine DTOs, service contracts, business validation, resource-level authorization, and mapping from persisted results to response DTOs. |
| DAL | Own gym-scoped queries, ordering, relational constraints, revision checks on writes, and the publication transaction. Repository contracts use Entity types, not BLL DTOs. |
| Entity | Model routines, days, and exercises without HTTP or EF Core dependencies. |

Invalid publication content returns a validation error. A missing or out-of-gym trainer target returns `404`; a revision mismatch returns `409`. Authentication, role, and coverage denials follow existing API conventions.

## Acceptance checks

- [ ] An enabled trainer in the same gym can publish to an existing profile without being assigned to the student; another gym, a disabled trainer, and unauthorized roles cannot do so. Publishing does not create or replace a student profile.
- [ ] An eligible student sees only their own published routine in the saved order. A student without coverage cannot read it. A missing publication is shown as an empty state only when access is allowed.
- [ ] A routine with no days or with an empty day cannot be published. Editing the form without submitting does not change the current routine.
- [ ] Successful publication creates or replaces the current routine without exposing a history. A failed publication leaves the previous routine intact.
- [ ] A publication based on an outdated revision cannot overwrite newer work. Relational integration tests verify uniqueness and atomic replacement under competing requests, including first publication.
- [ ] Duration-only and legacy repetition prescriptions round-trip for Trainer and eligible Student in saved order. Both/neither/nonpositive targets are rejected by BLL and storage; failed requests preserve the previous revision and content.

## Out of scope

Persisted drafts, duplicate student profiles, templates, a shared exercise catalog, exercise loads, repetition/duration ranges, file imports and attachments (including PDFs and spreadsheets), workout logging and progress tracking, AI suggestions, and published routine history. Applying a database migration is not part of this design approval.

References: [backend architecture](../../BackendArchitecture.md), [student profiles](../../Students.md), and [student coverage](../../Payments.md).
