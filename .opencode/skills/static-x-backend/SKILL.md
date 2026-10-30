---
name: static-x-backend
description: "Trigger: Static-X backend, API, BLL, DAL, ENTITY, .NET design or coding. Apply project-specific layering, tenancy, and trainer-approved AI rules."
license: Apache-2.0
metadata:
  author: Static-X
  version: "1.0"
---

## Activation Contract

Use for Static-X backend design, implementation, and review. Read the full [backend guide](../../../Docs/Guia_Backend_Senior_NET_Capas.md) before backend design or coding; use it for details not overridden below.

## Hard Rules

- Precedence: explicit user decisions override this skill and conflicting guide instructions. Preserve the authorized **four physical traditional projects** under `src/Static_X.{API,BLL,DAL,Entity}` and tests under `tests/Static_X.Tests`, registered in `Static-X.sln`. Do not introduce Clean/Onion, CQRS, handlers, or new layers.
- Enforce API → BLL → DAL → Entity. Allow BLL/API → Entity and API → DAL for composition. Never allow DAL → BLL/API or BLL → API. BLL consumes DAL repository interfaces, not implementations or DbContext.
- Put BLL services in `Services`, all six contracts in `Interfaces`; DAL repositories in `Repositories/Interfaces` and `Repositories/Implementations`, context/migrations in `Persistence`, and outbox in `Persistence/Models`. API owns `Auth`, `Controllers`, the scoped activation outbox hosted worker in `Integrations`, and `Integrations/Email` implementing BLL's `IActivationEmailSender`.
- For all future API controllers, use an explicit public constructor, store injected dependencies in private readonly fields, and assign them with `this.field = parameter;`, following `ActivationController`. Do not use primary constructors or migrate existing controllers outside the explicitly requested scope.
- Put all specific HTTP/business DTOs in BLL `DTOs/{Accounts,Activations,Gyms,Students}`, one conventional class per file with explicit properties and constructors. Preserve JSON, DataAnnotations, defaults, and nullability. Public business services consume DTO field groups and return DTOs, never entities, even in nested results; BLL maps entities and validates independently of MVC. Keep trusted gym/target identifiers, authorized roles, and cancellation separate; client DTO fields never authorize membership/roles. Do not wrap simple identifiers, enums, scalar results, or cancellation-only operations artificially. API owns transport validation/status mapping, not business entity mapping; preserve explicit Identity/auth/composition access to `GymAccount` and role enums. Keep framework errors, entities and internal Entity-owned `ActivationDelivery` in their existing layers; DAL remains entity-based, never referencing BLL DTOs.
- Scope tenant-owned reads and writes to their gym using trusted server-side identity/membership. Never accept a client-supplied gym ID alone as authorization; prevent cross-gym access, including indirect references and updates. The authorized exception is global `SuperAdmin = 4`, with `GymId = null` enforced by a database check constraint and unique filtered global email index. Only that role can provision gyms and reissue pending GymAdmin invitations over CSRF-protected HTTP. It has no implicit student-data access. Global login omits/nulls `gymId`, never searches tenants, and `/me`/cookie validation enforce persisted scope and role. Keep tenant role values 1–3. No fake gym, public global registration, startup bootstrap, activation CLI, local pickup, or operational terminal account-management interface is permitted. First SuperAdmin provisioning is a separately approved direct Development database operation, not shipped code. This supersedes historical CLI references.
- Put AutoMapper profiles in BLL `Mappings`, one per actual responsibility. API composition registers once with a BLL profile marker, scanning only BLL; only response-mapping services consume `IMapper`. Map persisted results through explicit DTO constructor parameters. Keep validation, normalization, trusted gym/role selection, and authorization in services, not profiles. Do not add automatic reverse/request/Identity response maps, controller mapping, DAL/Entity AutoMapper dependencies, or artificial activation profiles. Validate real profiles and preserve null/list behavior in tests. Keep `AutoMapper:LicenseKey` external (`AutoMapper__LicenseKey`), retain license diagnostics, and never assume production eligibility. Add concise Spanish XML summaries to mapping classes and constructors.
- AI may suggest routine changes; only a trainer can approve and apply them. Never auto-modify routines from AI output or treat suggestions as authorization.
- Entity owns pure `Account`/`AccountRole` and the explicitly authorized `GymAccount : IdentityUser` exception with an ASP.NET Core framework reference. Do not claim Entity is entirely framework-free or add EF/HTTP/provider coupling. `ActivationDelivery` belongs to Entity but is not an EF entity. Preserve existing CLR namespaces, migration IDs/Up/Down, mappings, Data Protection names, and HTTP contracts; use assembly/project-reference checks rather than namespaces as ownership evidence.
- Document every new or modified BLL service and DAL repository implementation with a concise, single-line Spanish XML `<summary>` immediately above the class and each method, including private helpers and explicit constructors when present. Explain verified responsibilities in neutral/professional Spanish; preserve existing security/operational comments and the language of unrelated comments and literals. Apply this language exception only to these summaries, without behavior changes or unrelated refactoring.

## Decision Gates

| Situation | Action |
| --- | --- |
| Guide conflicts with selected layering | Follow the user-selected architecture above; flag the conflict when relevant. |
| Change exceeds the authorized physical tree or reference graph | Ask for separate authorization before changing the scaffold. |

## Execution Steps

1. Inspect existing solution and relevant code before proposing changes; preserve unrelated structure.
2. Trace HTTP contract → BLL service interface → BLL implementation → DAL repository interface → DAL implementation → ENTITY model as applicable.
3. Validate transport in API, business rules and gym ownership in BLL, and integrity in storage. Use explicit DTOs, parameterized queries, async I/O, cancellation where appropriate, and consistent error handling without leaking internals or secrets.
4. Run `dotnet build "Static-X.sln"` and `dotnet test "Static-X.sln" --no-build`, including architecture/model checks, tenant isolation, global authorization, and HTTP/activation/worker regressions. Never use real SQL Server writes or Resend delivery as migration verification.

## Output Contract

Report affected divisions, decisions or assumptions, executed checks, and any unverified behavior. Do not claim tests passed unless run.

## References

- [Full backend guide](../../../Docs/Guia_Backend_Senior_NET_Capas.md) — local source of detailed conventions, subject to the precedence above.
- [Physical architecture](../../../Docs/BackendArchitecture.md) — project ownership, compatibility, and package allocation.
