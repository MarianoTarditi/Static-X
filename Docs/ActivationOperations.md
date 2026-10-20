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
dotnet run --no-launch-profile --project .\Static-X\Static-X.csproj -- provision-gym --idempotency-key opaque-request-001 --gym-name "Example Gym" --admin-email admin@example.invalid
dotnet run --no-launch-profile --project .\Static-X\Static-X.csproj -- reissue-activation --gym-id 7 --account-id account-id-from-approved-record
dotnet run --no-launch-profile --project .\Static-X\Static-X.csproj -- dispatch-activation
```

Schedule **only** the last command in a restricted, non-overlapping scheduler job at an appropriate interval. Each invocation attempts one due invitation and exits; exit code `0` means a job/operation completed, `3` means no eligible job or reissue target, `2` means invalid options or missing configuration, and `1` means an operational failure. Output intentionally contains no recipient, gym identifier, token, API key, or provider exception.

Retry provisioning with **the same key and identical gym name/email** after a transient failure; the persisted key maps back to the same gym, while conflicting details are rejected. A successful provision queues an invitation but does not send it. If the invitation expires or must be replaced, an operator must explicitly reissue it for the verified gym ID and account ID, then allow the scheduler to dispatch. Reissue revokes the previous token and supersedes pending delivery; never expose this command as an HTTP route.

## Verify without delivery

`dotnet build .\Static-X.slnx` and `dotnet test .\Static-X.slnx` do not invoke the operator commands. CLI tests use in-memory fakes; they never write SQL Server or call Resend. EF migration listing and model-drift checks can be run offline with `--no-connect` where supported; do not use `database update` without explicit migration approval. Production SQL Server behavior, scheduler permissions and frontend fragment handling require separate operational validation.
