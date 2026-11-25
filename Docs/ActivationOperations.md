# Operate global administration and gym activation safely

Gym administration is HTTP-only. A global **SuperAdmin** lists gyms and pending administrators, provisions gyms, and reissues pending administrator invitations. The HTTP host automatically dispatches the persisted outbox. There is no activation CLI, pickup transport, scheduler command, public registration, startup seeding, or shipped account-management script. The frontend is separate future work; this change supplies no UI.

## Deployment prerequisites

1. Review the new `20261028230341_AddGlobalSuperAdmin` migration and its SQL against an explicitly approved database. Apply it only through the separately authorized deployment process. Startup never creates or migrates databases. Existing migration IDs and bodies are preserved. Rollback refuses to proceed while global accounts exist; never invent a gym to make rollback succeed.
2. Keep `ConnectionStrings__Static-X` in the deployment secret store. Use only the permissions needed by the API. Never put connection strings or credentials in command arguments, tracked JSON, logs, or screenshots.
3. Keep `Activation__KeyRingPath` as an existing durable absolute directory **outside the application/content root**. Restrict access to the service identity and approved operators; back it up and restore it with its database. Preserve the Data Protection application `Static-X.Activation`, delivery purpose `Static-X.ActivationDelivery.v1`, and per-invitation purpose. Do not replace or rotate away existing keys when deploying this change: pending protected tokens depend on them.
4. Set `Activation__FrontendUrl` to the approved HTTPS activation page, without user info, query, or fragment. Configure `Resend__ApiKey` and `Resend__FromAddress` externally and verify the sender/domain and sending permissions. The sender must be a plain email address, not a display-name expression. There is no fallback recipient or local delivery transport.
5. Keep the existing external AutoMapper license configuration and applicable production eligibility checks described in [BackendArchitecture.md](BackendArchitecture.md#package-and-operator-license-setup).

**Starting the HTTP host can send previously queued mail.** Confirm the database, key ring, frontend destination, provider, and sender before startup. Do not start an exploratory host against a live or shared outbox. Missing or unsafe delivery configuration causes a sanitized warning and a 30-second retry delay **before leasing**, not silent acceptance or delivery elsewhere. The worker uses a fresh asynchronous service scope per attempt; it waits 1 second after a processed job and 10 seconds when idle, propagates host cancellation, and waits 30 seconds on operational failures. Existing durable leases, retry limits and provider idempotency keys remain authoritative across host restarts and multiple instances. Monitor prolonged warnings and outbox backlog through restricted operational diagnostics; never log bearer links or provider exceptions.

## First SuperAdmin: separately approved Development database operation

No first account is created by this implementation. Before any later direct database provisioning:

- Obtain explicit confirmation of **Development**, the actual SQL Server/database, and the intended identity. A local environment label alone does not prove database isolation. Confirm credentials privately; do not accept secrets in chat, command arguments, shell history, or recordings.
- Review/apply the approved schema separately, retain backups and the correct durable key ring, and verify there is no conflicting global normalized email. Use a restricted connection and an explicit transaction; require exactly the intended affected row and verify nonsecret identity metadata afterward.
- Use ASP.NET Core Identity's compatible password hasher and configured normalization/password policy, not custom cryptography or a plaintext/default password. Prepare a unique account ID and username/normalized username, normalized email, fresh security/concurrency stamps, confirmed email, `LockoutEnabled = true`, `AccessFailedCount = 0`, and the usual safe Identity defaults. Persist role text **`SuperAdmin`** and **SQL NULL `GymId`**. Never create a placeholder gym or reuse an invitation to elevate a tenant identity.
- The operation and credential transfer are outside shipped application code and require their own authorization. Do not add a bootstrap endpoint, seed, migration data insert, terminal account manager, or reusable account-creation script. Production provisioning policy remains outside this Development authorization.

## HTTP workflow

Use trusted HTTPS and the same cookie session throughout. Fetch `GET /api/account/csrf` and put its returned `token` in `X-CSRF-TOKEN` on every POST. Cookies remain Secure, HttpOnly, SameSite Strict. After login, fetch a **new authenticated** CSRF token; the anonymous token is not interchangeable. Do not log request bodies containing passwords or tokens.

### Global login and current identity

`POST /api/account/login` accepts:

```json
{ "email": "superadmin@example.invalid", "password": "<privately supplied password>" }
```

Omitting `gymId` or supplying `null` searches only the global SuperAdmin scope. A positive `gymId` searches only that gym; there is no fallback across tenants or from a tenant to global. Zero/negative selectors are invalid. Failure remains generic; confirmed-email and Identity lockout checks still apply. `GET /api/account/me` returns the persisted identity with `gymId: null`, numeric `role: 4`, and no credentials. Request role fields cannot elevate privileges. SuperAdmin has no implicit access to `/api/students`.

### Log out the current client session

Any authenticated role, including global SuperAdmin and all tenant roles, can call `POST /api/account/logout` with **no request body**. Send the current authentication and antiforgery cookies and a freshly fetched **authenticated** `X-CSRF-TOKEN`. Success is an empty `204 No Content` with `Cache-Control: no-store` and an expired application authentication cookie. The same client's subsequent `GET /api/account/me` returns 401.

Anonymous POST requests return 401. Missing, invalid, or pre-login CSRF tokens on authenticated requests return 400 **without signing out**. `GET /api/account/logout` returns 405 and does not sign out.

Logout only instructs this client to delete its application cookie; it does not change passwords, roles, gym membership, lockout counters, or Identity stamps, and does not queue mail. Other independently logged-in sessions for the same account remain valid. A copied or stolen cookie is **not revoked server-side** by this operation. The antiforgery cookie is separate: before logging in again, fetch a fresh anonymous token from `GET /api/account/csrf`, then refresh it again after login. Logout returns neither a CSRF token nor a replacement authentication cookie. See `src/Static_X.API/Account.http` for the request sequence.

### Change your own password and invalidate all sessions

Any authenticated role, including global SuperAdmin, can call `POST /api/account/change-password` with a fresh **authenticated** CSRF token and body `{"currentPassword":"<current password>","newPassword":"<replacement password>"}`. The validated cookie selects the account and its persisted scope; client account, gym, or role fields cannot choose a target. Password whitespace is preserved. Identity verifies the current password and enforces its configured replacement-password policy.

Success is an empty **204**, `Cache-Control: no-store`, and an expired current application cookie. Identity saves the new password hash and security stamp together under optimistic concurrency. Every application-cookie request explicitly validates the persisted stamp before the existing periodic Identity validator and scope/role checks, so other sessions and copied old cookies receive **401 on their next request**, even at the cookie issuance timestamp. This does not cancel requests already authenticated and in flight. Logout remains current-client-only.

Anonymous requests return 401. Missing, invalid, or pre-login CSRF tokens return 400 without changing credentials or signing out valid sessions. Missing/null/empty passwords return validation 400; wrong current passwords, rejected replacement passwords, or concurrency conflicts return generic 400 Problem Details (`Invalid password change request.`), never Identity errors or credentials. Failed changes leave persisted password/stamp/counters unchanged. After success, fetch fresh **anonymous** CSRF, explicitly log in with the new password and original gym selector (omitted/null for global), then fetch authenticated CSRF again. No automatic sign-in, reset/recovery operation, invitation, or mail is generated. See `src/Static_X.API/Account.http`.

Cancellation is propagated through the request layers and checked before Identity, but `UserManager.ChangePasswordAsync` has no request `CancellationToken` parameter. A disconnect or cancellation after it begins does not guarantee rollback. If the outcome is unknown, do not assume the old password or existing sessions remain valid.

### Provision a gym and pending administrator

Only a validated SuperAdmin session can call `POST /api/gyms/provision`:

```json
{ "key": "opaque-request-001", "gymName": "Example Gym", "email": "admin@example.invalid" }
```

The opaque idempotency `key` belongs in the JSON body (maximum 128 characters); do not use credentials or email addresses as keys. Gym name is required and at most 200 characters; the plain administrator email is required and at most 256 characters. BLL enforces these independently of HTTP validation.

Success is `200 OK` with the gym ID as a JSON integer, both for first provision and an identical retry. The gym, pending GymAdmin, invitation, and protected outbox entry are persisted atomically. The pending administrator has no password and cannot sign in until activation. No token, bearer URL, password, or Identity entity is returned. Reuse the same key and normalized-identical details for retries; different details return 409. Invalid fields return 400; unavailable provisioning configuration returns a sanitized 503. A response confirms persistence, not provider acceptance.

### Select a gym and pending administrator

These read-only GET requests require a validated SuperAdmin cookie, but **no CSRF header**. They do not create invitations, change account state, or queue/send email. Responses use `Cache-Control: no-store`; treat administrator emails as restricted administrative data.

`GET /api/gyms?page=1&pageSize=20` lists **all** gyms, including those without pending administrators, in ascending gym ID order:

```json
{ "items": [{ "id": 2, "nombre": "Example Gym" }], "page": 1, "pageSize": 20, "totalCount": 1 }
```

`GET /api/gyms/2/administrators/pending?page=1&pageSize=20` lists eligible pending administrators in ascending account ID order:

```json
{ "items": [{ "accountId": "opaque-account-id", "email": "admin@example.invalid" }], "page": 1, "pageSize": 20, "totalCount": 1 }
```

This legacy account selector requires the selected gym, role GymAdmin, enablement, unconfirmed email, a **null** password hash (an empty hash is not null), and a matching provisioning request. It does not inspect invitation history or the resend interval. The lifecycle listing below is the authoritative queue-state view; resend additionally requires a historical invitation/outbox pair and the one-hour interval. No tokens, links, roles, Identity stamps, password hashes, or student data are returned by the legacy selector.

| Condition | Result |
| --- | --- |
| No validated session / tenant role | 401 / 403 respectively; client role or gym fields cannot grant access. |
| Existing gym with no eligible administrators | 200 with empty `items` and `totalCount: 0`. |
| Page beyond the last item | 200 with empty `items` and the full matching `totalCount`. |
| Positive nonexistent gym | Generic 404 Problem Details. |
| Invalid/noninteger/nonpositive gym or invalid pagination | Generic 400 Problem Details or Validation Problem Details. |
| Unexpected query failure | Generic 500 Problem Details, without database/provider details. |

`page` defaults to 1 and must be positive; `pageSize` defaults to 20 and must be 1–100. Noninteger/overflowing values or an offset `(page - 1) * pageSize` above `int.MaxValue` are rejected, never clipped. Storage performs bounded paging and a separate count; concurrent changes can affect these separate reads, so no snapshot consistency is promised. Cancellation is propagated.

The selected gym is an administrative target **after** SuperAdmin authorization, never proof of tenant membership. These queries grant no student access. Listing is advisory: an administrator activated after listing is no longer eligible, and reissue still returns 404.

### Reissue a pending administrator invitation

Only SuperAdmin can call `POST /api/gyms/{gymId}/administrators/{accountId}/invitation` with **no request body** and a fresh session-bound CSRF header. Obtain the exact target from the administrative queries above or an approved administrative record. The gym/account route is target selection, never authority. Success is 204; a missing, wrong-gym, non-administrator, global, already confirmed, or password-bearing target returns 404. Invalid bounded identifiers return 400; unavailable key configuration returns 503.

Reissue requires **one hour from the last persisted queueing**, including sent, cancelled and expired invitations. An earlier request returns sanitized 409 without writing. Reissue revokes previous links and supersedes pending delivery in the same transaction. Old tokens never become valid again. The outbox worker delivers the new invitation automatically. Do not drain unrelated outbox jobs or change invitation/Identity columns manually to simulate activation.

### Invite a Trainer or an explicitly selected Student

This tenant route is separate from gym provisioning and global administration. Deploy **`20261111012132_AddTenantAccountInvitations` (UNAPPLIED here)** after `AddPasswordRecoveryOutbox` through a separately authorized process. Starting a configured host can dispatch queued invitations; do not start one as an exploratory verification step.

Authenticated, CSRF-protected **`POST /api/accounts/invitations`** accepts:

```json
{ "key": "opaque-request-001", "email": "recipient@example.invalid", "role": 3, "studentId": 23 }
```

GymAdmin may invite Trainer (2) or Student (3); Trainer may invite Student only. Student, SuperAdmin, grants of GymAdmin/SuperAdmin and Trainer-to-Trainer management are forbidden (403). Authority comes from the validated session, never request gym/actor-role fields. For Trainer, omit/null `studentId`; for Student, select an existing active ficha in the session gym. No new ficha is created or inferred from contact email. A ficha can remain independent indefinitely; contact email and the explicitly chosen login recipient may differ.

The opaque key is required, at most 128 characters. Email is trimmed, validated as a plain address, bounded to 256 and normalized by Identity. Student selection must be positive. Invalid fields return generic 400, missing/foreign fichas indistinguishable 404, and inactive/already-linked/conflicting account/key targets 409. Success is **200 with only `{"accountId":"opaque-account-id"}`**, no-store, confirming atomic persistence rather than mail delivery. There is no raw token, password, bearer URL, automatic login or forced administrator password.

The tenant audit uniquely keys `(GymId, Key)` and snapshots normalized recipient, role, explicit student and account. Same key/input returns the original account ID even after activation or cancellation without generating mail or restoring a link. Changed input under that key conflicts. A new key may reissue only the same pending, enabled, same-gym/role/email/explicit-link account with matching tenant-invitation provenance, **after one hour from last queueing**. Earlier attempts return 409. Historical passwordless accounts cannot be adopted. Reissue atomically revokes older invitations and erases superseded pending delivery secrets without changing credentials, stamps or lockout counters. Activated accounts conflict; roles cannot be changed by reissue.

### List, resend and cancel invitation queue state

Use the existing validated cookie and a fresh authenticated CSRF token on **every POST**. GETs require authentication but no CSRF header. All lifecycle responses are no-store. `role` is explicit target selection, never authority; tenant gym selection comes only from the validated session.

| Caller | Route | Result |
| --- | --- | --- |
| GymAdmin (Trainer/Student); Trainer (Student only) | `GET /api/accounts/invitations?role=3&offset=0&pageSize=25` | 200 with the latest persisted invitation per pending account |
| Same tenant authority | `POST /api/accounts/invitations/{accountId}/resend?role=3` | Bodyless; 204 queues a replacement, 409 if less than one hour |
| Same tenant authority | `POST /api/accounts/invitations/{accountId}/cancel?role=3` | Bodyless; 204, including repeat cancellation |
| SuperAdmin only | `GET /api/gyms/{gymId}/administrators/invitations?offset=0&pageSize=25` | 200 with latest GymAdmin invitation states in the selected gym |
| SuperAdmin only | `POST /api/gyms/{gymId}/administrators/{accountId}/invitation` | Existing bodyless resend route; same one-hour rule |
| SuperAdmin only | `POST /api/gyms/{gymId}/administrators/{accountId}/invitation/cancel` | Bodyless; 204, including repeat cancellation |

The lifecycle page contains `items` and `totalCount`. Each item exposes only `accountId`, `email`, numeric `role`, `status`, UTC ISO-8601 `queuedAt`, `expiresAt` and `nextResendAt`. Status is **Pending**, **Expired**, or **Cancelled**; Pending means persisted queueing, not successful email delivery. Cancellation takes precedence over expiry. Activated/password-bearing accounts are excluded. Pending disabled accounts can remain visible and can be cancelled, but cannot receive a replacement while disabled. Student targets retain explicit same-gym ficha/provenance checks; resend additionally requires an active ficha.

Offset defaults to 0 (0–100000); page size defaults to 25 (1–100). Results are ordered by opaque account ID ascending. Beyond-end pages return empty items with the matching count. Count and page are separate bounded reads, not a snapshot guarantee. Missing gyms on the new lifecycle list return an empty page, unlike the legacy pending-administrator selector. No tokens, bearer links, delivery secrets, request keys, stamps or password data are returned. Treat recipient addresses as restricted administrative data.

Unauthorized sessions return 401; forbidden actor/target-role pairs return 403. Malformed input/pagination returns 400; missing, foreign, wrong-role, activated, password-bearing or otherwise ineligible action targets return the same generic 404. Missing/invalid/pre-login CSRF returns 400 without mutation. Cooldown returns sanitized 409, with the next eligible time available in the authorized listing. Delivery-key configuration failures return 503; unexpected errors are sanitized and contain no database or recipient details.

**Cancellation revokes every unconsumed link and clears pending delivery secrets, retry scheduling and lease ownership atomically.** It preserves the account, its enablement, credentials, lockout, stamps, immutable audit history and ficha/link. It never deactivates or signs in anyone. Cancelling an activated account returns 404 and cannot change its password. Deliberate re-invitation is allowed after the same cooldown: use a **new** tenant request key and the original recipient/role/explicit ficha, or the authorized resend route. Old keys are retries only and never resurrect cancelled links. The old link remains invalid after replacement. Cancellation, replacement and activation share the existing transaction-owned gym mutex; competing resends cannot both queue within the interval.

This implementation is **schema-free**: immutable `ActivationOutbox.CreatedAt` supplies last queue time across all historical invitations, regardless of expiry/revocation/send state. It does not infer queue time from edited expiry or erase history. Accounts without a historical invitation/outbox pair cannot be adopted or resent through lifecycle actions. Preserve those metadata rows. No lifecycle migration is generated or applied; the existing global/tenant invitation schema must already be deployed through its separately approved process. Retain the durable key ring, purposes, configuration and provider safeguards above.

The worker rechecks persisted invitation/lease validity immediately before calling the sender. Cancellation cannot retract email already accepted or already in flight after that final check; even if such mail arrives, its cancelled link cannot activate. SQLite/in-process HTTP and fake-provider checks do not certify SQL Server concurrency or real delivery. See `src/Static_X.API/Invitations.http` for requests; no UI, live provider operation or deployment is performed here.

Activation uses the existing anonymous CSRF-protected route below, with only token/password input. The persisted expected role selects the grant; Student activation/delivery additionally require the active same-gym explicit link and enabled identity. Deactivation pauses a still-valid pending invitation without consuming it. Reactivation does not activate the account, but the unchanged token may work until expiry. Login, current-cookie lookup, password change and recovery reject disabled accounts. See [ficha access synchronization and rollback](Students.md#explicit-account-linkage-and-access-synchronization). The bounded Trainer list/state controls below are separately approved; login-email, role and profile editing remain deferred.

### Manage Trainer account access

Only a validated **GymAdmin** session can list, deactivate or reactivate Trainer accounts in its own gym. Trainer, Student and global SuperAdmin receive 403; no validated session receives 401. Client gym/role fields cannot select authority. No profile, login-email, role or gym editing, account deletion, automatic invitation/reissue or student access is added.

`GET /api/trainers?page=1&pageSize=20` needs no CSRF header and returns 200/no-store:

```json
{"items":[{"accountId":"opaque-trainer-id","email":"trainer@example.invalid","isEnabled":true,"emailConfirmed":false}],"page":1,"pageSize":20,"totalCount":1}
```

All Trainer accounts are included, even disabled, unconfirmed and passwordless accounts, ordered by opaque account ID ascending. An empty gym or page beyond the last item returns empty items with the matching count. Page defaults to 1 and size to 20; positive integer page, size 1–100 and a safe offset at most `int.MaxValue` are required independently in BLL. Invalid/noninteger/overflow input returns 400, not clamped values. Counts and items are separate bounded database reads, not a snapshot guarantee. The response exposes no credentials, stamps, roles, invitation details or student data.

Use a fresh authenticated `X-CSRF-TOKEN` with **bodyless** `PATCH /api/trainers/{accountId}/deactivate` or `/reactivate`. Success is empty 204/no-store, including repeats. IDs are nonblank opaque strings up to Identity's 450-character limit, not necessarily GUIDs. Invalid IDs return 400; missing, foreign-gym or non-Trainer targets (including the administrator itself) return the same generic 404. Missing/invalid/pre-login CSRF returns 400 without state or session changes. GET on either PATCH route returns 405. All action successes and early authentication/validation errors are no-store; unexpected failures return generic 500 and cancellation propagates.

Disabling atomically changes `IsEnabled` and rotates security and concurrency stamps. Current, independent and copied old application cookies fail on the next request; already authenticated in-flight requests are not cancelled. Repeated disable does not rotate stamps. Reactivation preserves the rotated security stamp and refreshes concurrency only on transition; old cookies and old recovery tokens stay invalid. A confirmed password-bearing account must explicitly log in again and remains subject to its existing lockout. Password hash, email/confirmation, role/gym, lockout counters/end, Student suspension marker and fichas are unchanged.

Pending Trainers remain unconfirmed and passwordless after reactivation. Disable pauses activation/delivery and blocks invitation reissue, password change and recovery. Reactivation neither confirms nor creates a password/invitation: only the unchanged, unexpired existing activation token can complete the existing activation route. Trainer state, invitation and activation writers share the existing gym mutex; stale Identity password writes fail optimistic concurrency rather than restoring access. SQLite/fake-provider tests are not real SQL Server concurrency or delivery certification. There is **no new migration**; existing tenant enablement columns are reused. See `src/Static_X.API/Trainers.http`. No deployed host was restarted or migration applied by this change.

<a id="administrator-chooses-a-password"></a>

### Invited account chooses a password

For an authorized local **Development** trial, `Activation__FrontendUrl` should point to `https://localhost:<API HTTPS port>/activate` on the same host as the API. Open the original invitation in a browser on that computer and choose a password. No settings are changed by this feature. The page and its two assets are not served in Production or Staging; the activation API remains available.

The page removes the `#activation=` fragment at script startup. Only after submission does it fetch the anonymous CSRF token and send the invitation and password in the JSON body of `POST /api/account/activation`. It does not sign in the account. JavaScript is required; after a reload or if JavaScript was disabled, reopen the original link. Do not copy the invitation's full URL into logs or support reports.

```json
{ "token": "<token from the invitation fragment>", "password": "<chosen password>" }
```

Success is `200 OK` with only the persisted activated account's positive gym ID, returned after the activation transaction commits:

```json
{ "gymId": 2 }
```

The token/password request is unchanged. Request gym, role, or account fields cannot select or override the returned context. Invalid, expired, revoked, or already consumed tokens return generic `400` Problem Details (`Invalid activation request.`), without gym context or secrets. A committed token cannot be replayed to recover a lost response; no recovery endpoint is added.

Activation is single-use, expires after 24 hours, applies Identity password rules, confirms email, and erases the outbox bearer secret. Responses retain `Cache-Control: no-store` and `Referrer-Policy: no-referrer`. Activation does not issue an authentication cookie or sign in automatically; `/api/account/me` remains unauthorized in the anonymous activation session. Fetch a fresh CSRF token, then log in with the returned `gymId` and administrator credentials; refresh CSRF again after login. Tenant roles retain their existing numeric values: GymAdmin 1, Trainer 2, Student 3.

## Offline verification and limits

### Recover a forgotten password

Review and deploy **`20261109221729_AddPasswordRecoveryOutbox`** separately before enabling recovery. This implementation prepares the migration only; it does not apply it, provision accounts, restart a host or send real mail. Preserve the existing external durable `Activation__KeyRingPath` and Data Protection application `Static-X.Activation` and keys. Never change credentials or configuration to test this workflow against a shared deployment.

Fetch anonymous `GET /api/account/csrf` and send `X-CSRF-TOKEN` with both POSTs. If already signed in, obtain a current authenticated CSRF token instead. All responses from the actions are no-store and no-referrer. Do not log request bodies, bearer links, fragment contents, provider responses or exceptions.

`POST /api/account/forgot-password` accepts `{"email":"member@example.invalid","gymId":2}`. Omitted/null `gymId` searches only global SuperAdmin; positive values search only that gym, with no fallback. Nonpositive selectors or invalid fields return 400. A confirmed, password-bearing account with a valid persisted scope/role is eligible even when locked; pending activation accounts are not. Every valid request receives **202** with exactly `{"message":"If an eligible account exists, password reset instructions will be sent."}`, including nonexistent, wrong-scope, unconfirmed and passwordless accounts. This confirms acceptance/queueing where eligible, **not delivery**. No credential, stamp, session or lockout state changes during forgot.

Forgot alone is limited to five attempts per remote IP per fixed 15-minute window, independent of email/account existence, with no queue. The sixth returns 429 with `Retry-After` between 1 and 900 seconds. This is **per-instance in-memory** protection: restarts reset it and multiple instances do not share permits. Do not trust arbitrary forwarded headers; configure trusted proxy handling separately before deploying behind a proxy. Login and reset do not use this policy. Invalid CSRF/fields can return 400; unavailable system dependencies return sanitized 503. Key-ring shape/existence and protection are checked before account lookup for all valid inputs. Email destination/provider configuration is not required to durably accept forgot.

To enable dispatch later, the authorized operator supplies external **`PasswordRecovery__FrontendUrl`**, an absolute HTTPS page without user info, query or fragment, plus the existing `Resend__ApiKey` and plain `Resend__FromAddress`. No values are installed by this change. Missing/unsafe configuration blocks dispatch **before leasing** and produces only sanitized worker warnings. The future frontend (not provided) must read `#reset-password=<opaque protected envelope>`, remove it without telemetry/referrer leakage, and POST `{"token":"<envelope>","newPassword":"<chosen password>"}` to `/api/account/reset-password`. Preserve password whitespace.

Recovery expires **30 minutes from request creation**, enforced by persisted expiry and an independently configured Identity token provider. The token selects a protected persisted request/account; extra client account/gym/role fields cannot override it. Success is empty **204** with no automatic login. A matching logged-in caller receives an expired application cookie; a different logged-in identity stays signed in. All old cookies for the reset account receive 401 on their next request, even without clock advancement; requests already authenticated can finish. Explicit login with fresh CSRF and the new password is required. Reset preserves lockout end and failed-attempt count, so a blocked account remains blocked.

Invalid, malformed, tampered, expired, revoked, replayed, stale-stamp or policy-rejected resets return the same **400** Problem Details (`Invalid password reset request.`), without account context or Identity error details. Operational failure returns generic 503. Request consumption and Identity's hash/stamp write commit together; failed policy or persistence rolls back. Concurrent redemption has one winner. Identity operations do not accept a request cancellation token internally; cancellation is checked before them and transaction commit uses request cancellation, but an unknown client outcome still requires a fresh login rather than an assumption of rollback.

Repeated forgot requests coalesce the same eligible account's unexpired **unsent** request (no arbitrary cooldown); once sent, a new request may be queued without invalidating prior sent links. A successful reset invalidates the other requests. Protected delivery payloads are erased on confirmed send, terminal expiry, stale eligibility or consumption; metadata remains, so an already-sent link works after delivery-secret deletion.

Each scoped worker iteration runs a dedicated, cancellation-aware cleanup **before checking mail configuration**. It inspects at most 100 payload-bearing requests, oldest expiry first, and clears expired/terminal secrets or secrets whose account scope, role, confirmation, password presence or stamp no longer matches. Valid pending requests remain unchanged and cleanup never leases or sends. Cleanup needs database availability, not a usable key ring, sender or frontend configuration; it uses normal bounded worker delays/backoff, does not purge history, and guarantees no precise erasure deadline when the host/database is unavailable or a backlog exists.

Before the first external send, a conditional owner-bound update freezes the exact sender, recipient and final bearer link in the existing `ProtectedPayload` column as a versioned, encrypted message snapshot. Its separate purpose is `Static-X.PasswordRecoveryMessage.v1`; no plaintext bearer link is stored and no extra schema is needed. Unknown provider acceptance retries that frozen message with **`password-recovery/{requestId:N}`**, even if valid sender/frontend configuration or the account email changes later. Invalid configuration may pause retries but cannot rewrite their payload/key. Current account scope/stamp checks still block stale credentials. The frozen snapshot is erased by the same send/reset/cleanup paths as the initial protected token. Five-minute owner-bound leases prevent another worker from freezing/completing/releasing the active lease; process replacement resumes durable state. There remains a bounded race between final validity check and external send, not an exactly-once delivery claim. SQL Server concurrency, live provider behavior and future frontend handling are not proved by SQLite/fake-sender tests.

Run `dotnet build "Static-X.sln"` followed by `dotnet test "Static-X.sln" --no-build`. Tests use disposable SQLite databases, temporary key rings, in-process HTTP, controlled worker timers, and fake mail providers; they never provision deployed accounts, apply SQL Server migrations, or call Resend. SQL Server snapshot comparison and migration SQL generation run offline with dummy connection metadata. Test fixtures explicitly supply missing delivery configuration or fake senders so the new automatic worker cannot contact a real provider.

The normal ASP.NET Core host still receives its command-line configuration options unchanged. It exposes no operational activation commands. Actual SQL Server deployment/concurrency, service key permissions, mailbox delivery and the local browser-to-mailbox flow require separately authorized operational verification.
