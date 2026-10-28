# Operate gym activation safely

Only an authorized operator with access to the deployment environment may run these commands. There is no public registration or reissue endpoint. The HTTP server does not dispatch invitations; a separately scheduled invocation processes **at most one** due outbox job and exits.

## Before the first operation

1. Review and approve the EF Core migration script against the target database. Apply migrations manually under the deployment change process **before** invoking the CLI. Neither startup nor these commands create or migrate a database.
2. Restore the repository's local tools with `dotnet tool restore` from the repository root. This is for EF tooling, not for sending mail.
3. Set `ConnectionStrings__Static-X` to the approved SQL Server connection string in the deployment secret store. Grant only the necessary database permissions to the operator and scheduler identities. Never pass connection strings or credentials on the command line.
4. Create a durable directory **outside** the application/content root. Set `Activation__KeyRingPath` to its absolute path. Grant read/write access only to the service and operator identities; back up and restore the key ring with the database. Losing its keys makes queued invitations undecryptable. Protect its backups with equivalent access controls.
5. Set `Activation__FrontendUrl` to the public HTTPS activation page URL, without query string, fragment or user info. The frontend must read the `#activation=` fragment, submit the token and new password to `POST /api/account/activation`, and clear the fragment without sending it in telemetry or referrers. **This frontend behavior is not supplied by this unit.**
6. For dispatch **only**, configure `Resend__ApiKey` and `Resend__FromAddress` via the secret store. Verify the sender domain in Resend and ensure its SPF/DKIM configuration and production sending permissions are approved. Never use an unverified sender in production.

Do not place sensitive values in checked-in JSON, launch profiles, shell history, job arguments, or logs. Provisioning arguments include an email address (PII), so restrict process-list visibility, scheduler job definitions and their logs. The idempotency key is an operator-supplied opaque identifier, **not a password or token**. Store the key securely for retry; do not use an email address as the key.

## Invoke from the repository root

The examples use dummy identifiers only. Run only after the prerequisites above are approved:

```powershell
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- provision-gym --idempotency-key opaque-request-001 --gym-name "Example Gym" --admin-email admin@example.invalid
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- reissue-activation --gym-id 7 --account-id account-id-from-approved-record
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- dispatch-activation
```

Schedule **only** the last command in a restricted, non-overlapping scheduler job at an appropriate interval. Each invocation attempts one due invitation and exits; exit code `0` means a job/operation completed, `3` means no eligible job or reissue target, `2` means invalid options or missing configuration, and `1` means an operational failure. Output intentionally contains no recipient, gym identifier, token, API key, or provider exception.

Retry provisioning with **the same key and identical gym name/email** after a transient failure; the persisted key maps back to the same gym, while conflicting details are rejected. A successful provision queues an invitation but does not send it. If the invitation expires or must be replaced, an operator must explicitly reissue it for the verified gym ID and account ID, then allow the scheduler to dispatch. Reissue revokes the previous token and supersedes pending delivery; never expose this command as an HTTP route.

## Verify without delivery

`dotnet build .\Static-X.sln` and `dotnet test .\Static-X.sln --no-build` do not invoke the operator commands. CLI tests use in-memory fakes; they never write SQL Server or call Resend. Architecture tests compare the SQL Server model with the preserved snapshot and generate migration SQL offline. EF tooling uses DAL as the migrations project (`--project .\src\Static_X.DAL`) and API as startup (`--startup-project .\src\Static_X.API`), with `--context DAL.StaticXDbContext`; migration listing supports `--no-connect`. Never use `database update` without explicit migration approval. Production SQL Server behavior, scheduler permissions and frontend fragment handling require separate operational validation.

## First local administrator without a frontend or email provider

Use this **only with a separate, disposable Development database** that has the reviewed schema already applied. The following are manual operator steps, not startup seeding. No account or password defaults are supplied. Normal `dispatch-activation` still requires real Resend configuration; pickup flags never switch normal dispatch or the HTTP host to local delivery.

### 1. Prepare an isolated operator shell

Use a private Windows PowerShell 5.1 session without transcription, debugging, HTTP body tracing or terminal/session recording. Do not run the HTTP host or scheduler against production while experimenting. Confirm the actual server/database in the external configuration before acknowledging the local database below. **The confirmation flag is an operator acknowledgement, not automatic proof of database isolation.** The local command processes the next due job in the configured database, not a filtered email/gym: never point it at a production/shared outbox or drain unrelated jobs to find your invitation.

Keep the existing connection-string setting `ConnectionStrings__Static-X` in your private environment/secret store. Do not paste its value into commands or change tracked JSON. If the existing local appsettings point elsewhere, override them externally first. The application never creates the database or applies migrations for you.

```powershell
# Run from the repository root. These settings contain no credentials.
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Activation__DevelopmentPickupEnabled = 'true'
$env:Activation__DevelopmentDatabaseConfirmed = 'true' # Only AFTER checking the actual development database.
$env:Activation__FrontendUrl = 'https://localhost:7226/activate'
$env:Activation__DevelopmentPickupPath = Join-Path $env:LOCALAPPDATA 'Static-X-Development-Pickup'
```

The frontend URL is **nominal**: no frontend is supplied, started, opened or contacted. It only supplies the HTTPS base of the captured fragment link. Do not navigate to that link, paste it into a browser, print it, or send it to telemetry. The helper reads its token into memory and posts JSON to the existing activation endpoint.

Set `Activation__KeyRingPath` externally to an **existing durable private directory outside the repository/content root**, disjoint from pickup. Keep the same Data Protection application and key ring across CLI and HTTP processes. Do not rotate, empty, replace or delete an existing ring; back it up with its database. If preparing a brand-new disposable database/ring, an operator can create a dedicated owner-only ring before assigning the setting:

```powershell
$newRing = Join-Path $env:LOCALAPPDATA 'Static-X-Development-Keys'
if (Test-Path -LiteralPath $newRing) { throw 'Use the existing approved ring; do not replace it.' }
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetOwner($owner)
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object Security.AccessControl.FileSystemAccessRule($owner, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
[IO.Directory]::CreateDirectory($newRing, $acl) | Out-Null
$env:Activation__KeyRingPath = $newRing
```

Do **not** pre-create the pickup directory with default/inherited permissions. The adapter creates just that leaf directory under an existing parent with an owner-only Windows ACL before writing any secret. It refuses existing broadly accessible directories, files, reparse points/junctions/symlinks in the path, repository/content-root paths, profile/local-data roots, the global temp root, and overlapping key-ring paths. Its configured path must be a strict descendant of the current user's local application-data directory. It never creates broad home/profile trees, changes existing ACLs, or overwrites arbitrary files. Run CLI and HTTP under the intended same local identity, not multiple accounts sharing a directory. Administrators/OS operators remain privileged; this is not protection against a compromised current user or concurrent hostile filesystem replacement by that identity.

### 2. Provision, then capture exactly one invitation

```powershell
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- provision-gym --idempotency-key local-manual-001 --gym-name "Local Manual Gym" --admin-email admin@example.invalid
if ($LASTEXITCODE -ne 0) { throw 'Provision failed.' }
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- dispatch-activation-local
if ($LASTEXITCODE -ne 0) { throw 'Local dispatch did not complete.' }
```

Replace the dummy email with your intended development identity; no mailbox is contacted. Reuse the exact same opaque provisioning key and inputs for a retry. The local command is rejected outside Development, without **both** opt-ins or without a safe external pickup path, before resolving the dispatcher or leasing any job. The adapter independently repeats the Development/opt-in/path checks at every send. No fake Resend key/address is needed. Local dispatch suppresses provider/database log providers; its console response contains no private identifiers, paths or secrets.

One accepted delivery produces `<outbox-job-guid>.json` in the configured private directory. The record contains only the stable delivery key, recipient and fragment link; **it is a plaintext bearer secret at rest**, protected by filesystem permissions, not encrypted like the database outbox. Keep it out of source control, backups/sync, screenshots and logs; do not `Get-Content` it interactively. The writer uses private `CreateNew` temporary files and a no-overwrite rename. Identical retries reuse only matching contents; conflicting contents fail without replacing them. Failed capture is not marked sent and preserves the protected outbox token for the normal retry schedule. A crash may leave a private `.pending` file; remove only verified stale pickup artifacts, never key-ring files. Exit `0` indicates an attempted operation (as with normal dispatch), **not proof that a file was accepted**: confirm one `.json` exists without displaying its contents. Never fabricate, edit or reuse another invitation's record.

Obtain the login's **nonsecret gym ID** using an approved read-only query against that same development database; provisioning intentionally does not print it. For the dummy inputs above:

```sql
SELECT g.Id, g.Nombre
FROM Gimnasios AS g
JOIN AspNetUsers AS u ON u.GymId = g.Id
WHERE g.Nombre = N'Local Manual Gym' AND u.NormalizedEmail = N'ADMIN@EXAMPLE.INVALID';
```

Require exactly one matching gym. Do not guess `1`, query/print password hashes, protected tokens or invitation hashes, or modify confirmation/password columns. This read is only metadata discovery; activation still uses the real invitation, Identity password rules and normal HTTP route.

### 3. Activate and sign in over trusted local HTTPS

Trust your development HTTPS certificate using the normal SDK/operator process (`dotnet dev-certs https --trust`) if needed. Never disable TLS validation. Start the HTTP host **manually** in another private shell with the same development database and persistent key ring:

```powershell
dotnet run --no-launch-profile --project .\src\Static_X.API\Static_X.API.csproj -- --urls https://localhost:7226
```

Back in the configured operator shell, dot-source the helper. Enter the queried gym ID, the exact provisioned email and a new password at its secure prompt; do not put the password/token in command arguments, history, query strings or URL paths:

```powershell
. .\Docs\ActivateLocalAccount.ps1
# The helper stores cookies and refreshed authenticated CSRF headers only in memory.
# Do not print $StaticXSession or $StaticXHeaders.
$page = Invoke-RestMethod -Uri "$StaticXBaseUri/api/students?page=1&pageSize=20" -WebSession $StaticXSession
$page.items # Student data only; handle its PII appropriately.
```

The helper fetches anonymous CSRF cookies/token, posts the activation token/password as an in-memory JSON body, deletes the consumed pickup record, logs in with the same session, fetches `/api/account/me`, then refreshes CSRF after authentication. The normal secure, HttpOnly, SameSite cookies, gym credential selector, Identity validation and CSRF filters are unchanged. In-memory plaintext strings are unavoidable for HTTP serialization; BSTR storage is zeroed and credential variables are removed afterward, but this is not a guarantee of erasing managed memory. Stop recording/tracing **before** running it. On later sessions, or if activation succeeded but login failed, use `. .\Docs\ActivateLocalAccount.ps1 -LoginOnly` and enter the same credentials; no pickup is needed.

For student writes use `-WebSession $StaticXSession -Headers $StaticXHeaders` with the existing JSON contracts in [Students.md](Students.md) / `src/Static_X.API/Students.http`. Do not echo the headers. Close the operator shell to discard cookies, clear the pickup opt-ins when finished, and stop the manually started server. Delete any remaining expired/revoked pickup records securely under your local retention policy; deletion is not guaranteed forensic erasure, especially on SSDs/backups. Keep the durable ring if retaining its database. Reissue expired invitations through the existing verified gym/account operator command; old pickup files do not become valid again.

### Offline verification and limits

Automated tests use in-memory SQLite, temporary key rings, private pickup fixtures and an in-process HTTP server. They exercise real provisioning/outbox protection, local capture, anonymous CSRF activation, login, the administrator role and access to student listing without Resend or a frontend. No deployed database is connected, account provisioned, migration applied, external email sent, or persistent HTTP server started by verification.

Windows ACL behavior is exercised on the current Windows host. The writer also uses Unix directory `0700` / file `0600` on Linux/macOS and fails closed on unsupported platforms; Unix behavior requires separate verification, and the supplied helper is specifically Windows PowerShell 5.1. Use a private local filesystem under the actual user's local-data location; network/synced filesystems and compromised same-user processes are not a supported secret boundary. AutoMapper's existing external license configuration and production eligibility requirements remain unchanged; successful local tests do not establish production license compliance.
