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

Eligibility exactly matches reissue: the account belongs to the selected gym, has role GymAdmin, is not email-confirmed, has a **null** password hash (an empty hash is not null), and has a matching provisioning request for that gym/account. Invitation existence, expiry, revocation, consumption, and delivery status do not exclude an otherwise eligible account. No tokens, links, roles, Identity stamps, password hashes, or student data are returned.

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

Reissue revokes the previous invitation and supersedes its pending delivery in the same transaction. Old tokens never become valid again. The outbox worker delivers the new invitation automatically. Do not drain unrelated outbox jobs or change invitation/Identity columns manually to simulate activation.

### Administrator chooses a password

The future frontend must read the `#activation=` fragment from the emailed HTTPS link, send the token and chosen password in the body of `POST /api/account/activation` with anonymous CSRF protection, and clear the fragment without telemetry or referrer leakage. This UI is not supplied here.

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

Run `dotnet build "Static-X.sln"` followed by `dotnet test "Static-X.sln" --no-build`. Tests use disposable SQLite databases, temporary key rings, in-process HTTP, controlled worker timers, and fake mail providers; they never provision deployed accounts, apply SQL Server migrations, or call Resend. SQL Server snapshot comparison and migration SQL generation run offline with dummy connection metadata. Test fixtures explicitly supply missing delivery configuration or fake senders so the new automatic worker cannot contact a real provider.

The normal ASP.NET Core host still receives its command-line configuration options unchanged. It exposes no operational activation commands. Actual SQL Server deployment/concurrency, service key permissions, mailbox delivery and future frontend handling require separately authorized operational verification.
