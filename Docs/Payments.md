# Manual Student payments

This is a tenant gym pass ledger, **not** SaaS billing or a payment gateway. Deploy the unapplied `20261116000000_AddPayments` migration through the separately approved database process before enabling this host. No existing account is grandfathered and no financial history is fabricated. The live coverage gate is active immediately after deployment.

## Money, dates and coverage

All money fields are **integer ARS cents** (`amountCents`, `pricePer30DaysCents`): `100099` means ARS 1,000.99. The accepted tariff range is 1 through 10,000,000,000 cents. JSON fractional amounts cannot bind to this integer contract. Currency is fixed to ARS. Clients never submit a payment amount, discount or duration. Methods retain numeric JSON values: Cash=1, Transfer=2, Card=3. Status values: NoPayments=0, Current=1, ExpiringSoon=2, Expired=3.

Business dates use `America/Argentina/Buenos_Aires`, explicitly converted from the injected `TimeProvider` UTC clock. Date-only JSON uses `yyyy-MM-dd`; UTC audit uses ISO 8601 timestamps. Thirty days means fixed calendar days, not a calendar month. A November 15 first payment covers November 15 through December 14 inclusive; its exclusive end is December 15. Full last-day access is allowed until local midnight (03:00 UTC with the current Argentina rules).

First/lapsed coverage starts on `paidOn`. Early renewal extends from the preceding nonvoid exclusive end. Contiguous coverage retains its original aggregate start; a gap starts a new aggregate interval. Paid-on dates cannot be future or earlier than the last nonvoid payment's paid-on date. Same-day repeats are legitimate **only with a fresh preview and a new idempotency key**.

NoPayments means no nonvoid ledger entry. Expired means today is at/after the exclusive end. Otherwise, 0 through 5 days remaining until inclusive `paidThrough` is ExpiringSoon; more than 5 is Current. A backdated first payment can already be expired today.

## Tariffs

GymAdmin alone sets prices. The first tariff requires `initialEffectiveFrom`, which can be today or a past date, never a future date. No payment is accepted before it. Subsequent requests omit that field and always take effect **tomorrow in the business timezone**, even if requested far in advance of the intended use. Tariffs are immutable and unique per gym/effective date. A second request targeting the same date returns **409**; wait until the next business day to schedule another change. There is no replace/upsert route. Historical/backdated payment pricing uses the tariff effective on `paidOn`, never today's tariff. Each payment permanently records its price ID and exact amount.

## HTTP contract

All routes require the validated application cookie. All unsafe methods require the existing `X-CSRF-TOKEN`. Fetch a new request token from `GET /api/account/csrf` after login. Responses are no-store. Client gym/role fields never authorize access.

| Method and route | Authority / result |
| --- | --- |
| GET `/api/payments?page=1&pageSize=20&search=Ana&status=1` | GymAdmin/Trainer same-gym table; optional full-name substring/status filters; page size 1..100. |
| GET `/api/payments/prices` | Same-gym staff tariff history, including pending tomorrow. |
| GET `/api/payments/summary` | GymAdmin/Trainer; active same-gym ficha coverage counts and estimated remaining-month renewals. |
| GET `/api/payments/collections?from=2026-11-01&to=2026-11-30` | GymAdmin only; inclusive paid-on collections and separate void totals, split by method. |
| POST `/api/payments/prices` | GymAdmin; initial `{ "amountCents":100099, "initialEffectiveFrom":"2026-11-01" }`; subsequent `{ "amountCents":120000 }`. |
| POST `/api/payments/students/{studentId}/preview` | Same-gym staff; `{ "paidOn":"2026-11-15", "method":1 }`; trusted amount, extension/aggregate interval, revision and protected `previewToken`. No payment write. |
| POST `/api/payments/students/{studentId}` | Same-gym staff; same date/method plus `previewToken` and a new opaque `idempotencyKey` (1..128 characters). Returns 201 and history Location. |
| GET `/api/payments/students/{studentId}?page=1&pageSize=20` | Same-gym staff full ledger, newest sequence first, including void entries and internal operator audit. |
| POST `/api/payments/students/{studentId}/{paymentId}/void` | GymAdmin; `{ "reason":"Correction of accidental registration" }`, 1..500 characters; 204. |
| GET `/api/student/me/profile` | Student's explicitly linked ficha, not identity `/api/account/me`. |
| GET `/api/student/me/coverage` | Own current tariff, last nonvoid payment, paid-through/exclusive end/business date/status. |
| GET `/api/student/me/payments?page=1&pageSize=20` | Own history including void status, **without** internal audit reason/operator. |

The staff table returns `items`, `page`, `pageSize`, `totalCount`; each row includes the full name, ficha active flag, current price separate from last-payment amount, business reference date and coverage state. Count and bounded items are separate reads, not a snapshot-consistent dashboard. Search follows database collation. Histories are paged, not truncated permanently.

Expected errors: 400 invalid input; 401 unauthenticated; 403 forbidden; 404 absent/foreign selected ficha or missing own explicit link; 409 stale confirmation, reused key with different request, tariff effective-date collision, inactive ficha or invalid void order; 503 unavailable persistence. No reason, token or database exception detail is returned in error envelopes.

## Read-only analytics

Both analytics routes use only the authenticated gym and independently enforced BLL roles. Student and global SuperAdmin have no access; Trainer cannot read collections. They reuse payment no-store/error handling and do not modify the live coverage gate or existing paginated table/history behavior. Neither response contains names, account/student/payment IDs, contact data, operator audit or tokens. Monetary fields use integer ARS cents and `currency: "ARS"`.

### Coverage and estimated renewals

`summary` captures one Buenos Aires `businessDate` for the entire response. `activeStudents` counts only active fichas. The latest **nonvoid sequence** determines each ficha's aggregate exclusive coverage end; voiding the latest restores the preceding nonvoid entry. The four counts are mutually exclusive and sum to `activeStudents`:

| Field | Meaning |
| --- | --- |
| `noValidPayment` | No nonvoid payment exists. |
| `expired` | Exclusive end is on/before the reference date. |
| `dueSoon` | Exclusive end is tomorrow through today+6 inclusive: 0..5 days remaining until the **last included day**, end minus one day. |
| `current` | Exclusive end is after today+6. |

`estimatedRenewalCount` counts one potential renewal per active, currently covered ficha whose latest nonvoid aggregate end is **after today and before the first day of the next local calendar month**. The projected renewal date is that exclusive end, not the last included day. Prepaid extensions move this date forward and can remove a renewal from the month. Expired fichas and fichas without payments do not contribute. An end exactly on next month's first day is excluded.

`estimatedRenewalCents` sums the tariff with the latest effective date **on/before each projected renewal date**, including a scheduled tariff. It is not historical paid revenue, guaranteed income or debt. If any included renewal has no applicable tariff, the amount is `null`, not a partial/fake zero. With no renewals it is zero when a tariff applies today; otherwise it is `null`. The renewal count remains available when the amount is unknown.

### Collections and original-period corrections

`collections` requires both `from` and `to` as `yyyy-MM-dd`, with `from <= to` and **1..366 inclusive days**. Missing, malformed, default, reversed or excessive ranges return 400. Same-day and future date ranges are valid. It captures one `businessDate` and returns the exact requested bounds.

`validCount`/`validCents` include nonvoid entries; `voidCount`/`voidCents` separately show the original amounts of void entries, never negative refunds. `methods` always contains Cash=1, Transfer=2 and Card=3, each with those four fields, including zeros for absent methods. An empty period has zero totals. Inactive fichas remain included in historical collections. Both bounds filter **`PaidOn`**, not creation or void audit timestamps: backdated entries appear in their paid-on period, and a later void changes that same original period's valid/void split rather than creating a refund in the void date's period.

DAL executes latest-nonvoid selection, conditional counts, tariff projection and amount grouping in SQL; it does not load a dashboard page or query each ficha. Summary uses two aggregate reads and, only when there are no projected renewals, one tariff-existence read. Collections uses one grouped read. The captured date does **not** establish cross-query snapshot consistency: concurrent payment, void, ficha or tariff changes can be observed between summary reads. SQLite execution and offline SQL Server query translation are verification, not production SQL Server execution-plan or concurrency guarantees.

## Confirmation, concurrency and corrections

The preview token is protected with Data Protection purpose `Static-X.PaymentPreview.v1`. Preserve the existing durable shared Data Protection key ring/application name. The token binds gym, ficha, actor, business date, monotonic ficha revision and all calculated terms. It is not a client-editable price. Confirmation after a changed revision or business day returns 409 rather than silently accepting a new amount or interval. A void increments revision too, preventing an ABA reuse of an old preview.

Registration is serialized with the existing transaction-owned gym-row mutex; final persistence checks gym, active ficha, applicable tariff, predecessor and revision. The ledger has a unique `(GymId, IdempotencyKey)` plus immutable actor/target/request fingerprint. Exact retries reproduce the original payment ID/terms and **current truthful void state**, even if the ficha is now inactive or the payment was voided; no second extension is made. Changing actor, target, date, method or token with that key returns 409. Retain the exact original confirmation request for retries; requesting a replacement token is not an identical retry.

Only the last nonvoid payment in monotonic sequence can be voided. Voiding retains original terms, actor and UTC audit; it reveals the preceding nonvoid snapshot instead of subtracting thirty days across a gap. All void means NoPayments. Voiding never deletes the ledger. Down migration is guarded against dropping nonempty tariff/payment tables; archival/destructive rollback requires separate authorization.

## Access and boundaries

GymAdmin and Trainer can register/backdate and read same-gym data; only GymAdmin sets tariffs or voids. Global SuperAdmin has no tenant privileges. Fichas without an account can receive payments. New payments require an existing active same-gym ficha; history and administrative void remain available for inactive fichas. Payments never reactivate ficha/login, alter passwords/stamps, or bypass administrative blocking. Existing ficha disable/login synchronization is unchanged.

Student self resolution uses only persisted `(gym, AccountId)`, never contact email. A missing/incorrect-gym link cannot adopt a ficha. The live middleware checks persisted coverage on each authenticated Student operation, not a coverage cookie claim or `IdentityAccount.IsEnabled`. Staff are not coverage-gated. Only concrete actions may carry `AllowWithoutPaymentAttribute`: own profile/coverage/history and existing CSRF/login/logout/password/security/identity actions. No controller-wide or path-based exemption exists. Any future Student action defaults to coverage-required, including alternate routes. There is no routines controller added here. Coverage infrastructure failure yields 503, does not fake NoPayments and does not sign out the cookie; existing authentication failures retain their independent behavior.

## Ownership and verification limits

Entity owns `Payment`, `GymPassPrice`, payment enums and `Student.PaymentRevision`. BLL owns `DTOs/Payments`, `IPaymentService`/`PaymentService`, date arithmetic, validation, protected previews and `PaymentProfile`. DAL owns entity-based `IPaymentRepository`/`PaymentRepository`, transactional integrity, projections and migration metadata. API owns controllers, concrete-action gate metadata, transport errors and middleware. No new layer or package is introduced.

The current snapshot delegates to **frozen migration target metadata**, not the live runtime configuration, so subsequent model changes are still detected offline. Existing migration IDs and bodies remain intact. SQLite tests and SQL Server offline model/script checks do not establish deployed SQL Server concurrency behavior or database rollout readiness. No deployed database migration, production data writes, gateway call or email delivery is part of this change.
