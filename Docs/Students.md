# Student fichas: search, pagination, and active state

GymAdmin and Trainer can create, view, edit, deactivate, and reactivate student fichas in their authenticated session gym. A ficha is not an account: none of these operations changes login, credentials, roles, invitations, routines, or email delivery.

## HTTP contract

| Request | Behavior |
| --- | --- |
| `POST /api/students` | Creates an active ficha; returns 201 and Location. |
| `GET /api/students` | Returns page 1 of 20 fichas, including both states, ordered by unique Id. |
| `GET /api/students?activo=true` | Pages active fichas only. |
| `GET /api/students?activo=false` | Pages inactive fichas only. |
| `GET /api/students?page=1&pageSize=20&search=ana&activo=true` | Pages active matches across nombre, apellido, or email in the session gym. |
| `GET /api/students/{id}` | Includes inactive fichas. |
| `PUT /api/students/{id}` | Edits nombre, apellido, email only; preserves active state. |
| `PATCH /api/students/{id}/deactivate` | Sets activo=false; returns 200 with updated ficha. No body. |
| `PATCH /api/students/{id}/reactivate` | Sets activo=true; returns 200 with updated ficha. No body. |

Both PATCH actions are idempotent: requesting the existing state returns 200. Rows are never physically deleted. An inactive ficha still reserves its gym/email unique value; duplicates return 409, including when editing another ficha.

Student responses now contain the additional read-only JSON boolean `activo`, alongside `id`, `nombre`, `apellido`, and `email`. Clients enforcing exact schemas must accept the new field. Create/update requests do not accept active state or trusted gym/target fields; spoofed extra fields do not authorize or change state. Omitted `activo` on lists preserves the previous all-fichas behavior; invalid boolean values return 400. Filtering is performed in DAL before materialization, with no global query filter.

## List response and query rules

**Intentional breaking contract change:** the list endpoint returns an object instead of the previous top-level array. Clients must read `items` and page through the results; omission of `activo` still includes both states, not an unbounded list. Single-student responses and write routes are unchanged.

```json
{
  "items": [
    { "id": 23, "nombre": "Ana", "apellido": "Smith", "email": "ANA@EXAMPLE.COM", "activo": true }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

| Parameter | Default and validation |
| --- | --- |
| `page` | 1; positive 32-bit integer. `(page - 1) * pageSize` must fit a nonnegative 32-bit offset, checked safely in BLL. |
| `pageSize` | 20; integer from 1 through 100. Invalid values return 400, never clamp. |
| `search` | Optional; at most 320 characters **before trimming**. Trimmed blank means no search filter. Oversize returns 400. |
| `activo` | Optional strict boolean; omitted includes active and inactive fichas. Malformed boolean or numeric binding returns 400. |

Search matches a literal substring in `Nombre OR Apellido OR Email`, combined with `AND` gym and optional state predicates **before** counting or paging. `%`, `_`, and quotes are literal input, not user-provided SQL patterns or SQL. Values are parameterized. BLL trims and uppercases the term invariantly; DAL translates `ToUpper().Contains()` for each column. This provides case-insensitive matching for ordinary Latin text on SQL Server and SQLite without a provider-specific collation. Unicode case conversion and accent matching remain provider/collation-dependent (SQLite's built-in `upper` is ASCII-oriented); accent-insensitivity is not promised.

DAL awaits `CountAsync`, then `AsNoTracking().OrderBy(Id).Skip(offset).Take(pageSize).ToListAsync` on the same filtered query, forwarding cancellation to both. A page beyond the result set returns empty `items` with the requested page/size and the correct filtered count, including zero for no matches. No full-list materialization or in-memory filtering/counting occurs. BLL maps only the returned entities through the existing student AutoMapper profile and constructs the envelope manually.

### Consistency and performance limits

The two sequential queries are not snapshot-consistent under concurrent writes: totals and items may reflect different instants, and offset pages can shift between requests. No expensive read transaction is added. Uppercasing columns and unanchored substring searches can inhibit ordinary text-index seeks; deep offsets and exact counts also have costs. No performance improvement or deployed query plan is claimed, and this feature adds no schema/index/migration. SQLite execution tests and intercepted SQL Server commands verify SQL filtering, count, ordering, and bounds without connecting to SQL Server.

## Session security

- All operations require login (401) and GymAdmin or Trainer (Student receives 403).
- Every write, including bodyless PATCH, requires `X-CSRF-TOKEN` bound to the current authenticated cookie session. Fetch a fresh token after login.
- Resource scope comes only from the trusted session gym. Missing and cross-gym fichas return indistinguishable 404 responses; nonpositive integer ids return 400.
- Successes and errors use `Cache-Control: no-store`. Unexpected persistence failures remain generic 500 responses, not fabricated 404/success responses.

See [Students.http](../src/Static_X.API/Students.http) for requests.

## Schema rollout

`20261027023459_AddStudentActiveState` is the sixth migration. It adds `Alumnos.Activo bit NOT NULL DEFAULT 1`, making existing rows active without dropping/recreating the table. New entities and repository-created fichas also start active. Down removes only the new column (and loses state history); it does not delete fichas.

The five historical Up/Down bodies remain unchanged. The SQL Server model and snapshot are checked offline; SQLite tests check actual persistence/defaults and concurrency behavior. State writes mark only Activo modified; repeated calls still issue a write so deletion after retrieval produces a concurrency failure rather than false success. No RowVersion is introduced.

The active-state migration was generated in the preceding feature; the operator reports applying it, but that deployed state has not been independently verified. Search and pagination require **no new migration**. Deployment remains subject to the normal operator-reviewed process. Verification does not connect to SQL Server, inspect deployed migration status, or contact Resend.
