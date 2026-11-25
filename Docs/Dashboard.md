# Administrator dashboard

`GET /api/dashboard` returns the first dashboard section: persisted student ficha counts for the authenticated GymAdmin's gym. New sections can be added deliberately as their contracts become available; this release does not include routine, payment, or chart placeholders.

## HTTP contract

Use the existing application authentication cookie. This read-only GET does not require a CSRF token.

```http
GET /api/dashboard
```

```json
{"students":{"total":42,"active":36,"inactive":6,"activeWithoutAccount":12}}
```

| Condition | Result |
| --- | --- |
| Authenticated GymAdmin with a valid positive session gym | `200` with the counts |
| No valid authenticated session | `401` |
| Trainer, Student, or global SuperAdmin | `403` |
| Empty gym | `200` with all four counts equal to zero |
| Unexpected query failure | `500` with a generic ProblemDetails response |

The endpoint uses the existing administrative `Cache-Control: no-store` convention, including authorization failures. Existing persisted-account and security-stamp cookie validation remain unchanged. The controller also rejects a missing, malformed, or non-positive `gym_id`; client query values such as `gymId` or `gym_id` never select a gym or authorize access.

## Counting and ownership

- `total` counts all persisted `Alumno` fichas in the authenticated gym.
- `active` counts `Alumno.Activo == true`; `inactive` counts `false`.
- `activeWithoutAccount` counts same-session-gym fichas with `Alumno.Activo == true` and `Alumno.AccountId == null`, irrespective of contact email. Linked accounts are excluded even when activation or email confirmation is pending. This indicates active fichas that could still be invited to use the app.
- `total`, `active`, and `inactive` do not depend on linked accounts. No count depends on email matching, account enablement, payments, or routine publication. No names, emails, identifiers, or other personal fields are returned.
- API delegates to a dedicated BLL dashboard read service, which independently validates the trusted gym and actor role. DAL performs one filtered, server-side aggregate query and returns an immutable projection. BLL maps that result through `DashboardProfile`, discovered by the existing BLL-only AutoMapper registration.
- No schema change, migration, backfill, new package, or external service is required.

## Verification

`DashboardHttpTests` uses the real HTTP application, Identity cookies, and an isolated SQLite in-memory database. It covers mixed and uniform ficha states, an empty gym with foreign data, linked unconfirmed active fichas, unlinked active fichas even with a matching account email, inactive and foreign-gym exclusions, ignored query gym selectors, exact safe JSON, one aggregate student query, real mapping configuration, anonymous and denied roles, revoked cookies, invalid gym claims, and generic query failures. It also checks independent BLL scope and role rejection before any student query. Hosted email workers are removed only in this test host; no external database or email transport is used.
