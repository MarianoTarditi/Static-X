# Physical traditional backend architecture

`Static-X.sln` is the single active solution: four production projects and one existing test project, all targeting `net10.0`. This explicitly authorized migration replaces the previous single-project folder layout; it does not introduce Clean/Onion, CQRS, handlers, or new business behavior.

## Quick verification

```powershell
dotnet sln "Static-X.sln" list
dotnet build "Static-X.sln"
dotnet test "Static-X.sln" --no-build
```

The suite includes HTTP/login/CSRF, gym isolation, activation, delivery concurrency, CLI routing, project boundaries, API content-root discovery, and offline SQL Server snapshot/history compatibility. It does not apply migrations, connect to a deployed database, or send email.

## Source ownership

```text
Static-X.sln
src/
  Static_X.Entity/
    Static_X.Entity.csproj
    Entities/ Account.cs, Alumno.cs, Gimnasio.cs, GymAccount.cs,
              ActivationInvitation.cs, ActivationDelivery.cs, ProvisioningRequest.cs
    Enums/ AccountRole.cs
  Static_X.DAL/
    Static_X.DAL.csproj
    Persistence/
      StaticXDbContext.cs
      Configurations/ .gitkeep
      Migrations/ five preserved migrations plus AddStudentActiveState, designers, StaticXDbContextModelSnapshot.cs
      Models/ ActivationOutbox.cs
    Repositories/
      Interfaces/ IAccountRepository.cs, IActivationRepository.cs,
                  IActivationDeliveryRepository.cs, IGymRepository.cs, IStudentRepository.cs
      Implementations/ five matching implementations
  Static_X.BLL/
    Static_X.BLL.csproj
    Interfaces/ IAccountService.cs, IActivationService.cs, IActivationDispatcher.cs,
                IActivationEmailSender.cs, IGymService.cs, IStudentService.cs
     Services/ AccountService.cs, ActivationService.cs, ActivationDispatcher.cs,
                GymService.cs, StudentService.cs
     Mappings/ AccountProfile.cs, StudentProfile.cs, GymProfile.cs
    DTOs/
      Accounts/ LoginRequest.cs, CreateAccountRequest.cs, AccountResponse.cs, CsrfResponse.cs
      Activations/ ActivateAccountRequest.cs, ProvisionGymRequest.cs
      Gyms/ CreateGymRequest.cs, GymResponse.cs
       Students/ CreateStudentRequest.cs, UpdateStudentRequest.cs, StudentResponse.cs,
                 StudentListRequest.cs, StudentListResponse.cs
  Static_X.API/
    Static_X.API.csproj
    Auth/ GymClaimsPrincipalFactory.cs
    Controllers/ AccountController.cs, ActivationController.cs, StudentsController.cs
    Integrations/Email/ ResendActivationEmailSender.cs, DevelopmentPickupEmailSender.cs
    CLI/ ActivationCli.cs
    Program.cs, appsettings.json, appsettings.Development.json
    Properties/ launchSettings.json
    Account.http, Static-X.http
tests/
  Static_X.Tests/
    Static_X.Tests.csproj
    AccountHttpTests.cs, ActivationCliTests.cs, ActivationTests.cs,
    ArchitectureTests.cs, DtoContractTests.cs, TenantAccountTests.cs, TenantStudentTests.cs
```

## References and dependencies

| Project | Direct project references | Direct package/framework dependencies |
| --- | --- | --- |
| Entity | None | `Microsoft.AspNetCore.App` framework, solely for the explicitly authorized `GymAccount : IdentityUser` adapter |
| DAL | Entity | Identity.EntityFrameworkCore, EF.SqlServer, EF.Design, EF.Tools, and System.Security.Cryptography.Pkcs, all existing version `10.0.13` |
| BLL | DAL, Entity | AutoMapper `16.2.0`; `Microsoft.AspNetCore.App` framework for existing Data Protection/configuration use; explicit IConfiguration using |
| API | BLL, DAL, Entity | AspNetCore.OpenApi and EF.Design `10.0.13`; Resend `0.24.0`; Web SDK framework |
| Tests | API, BLL, DAL, Entity | Existing Test.Sdk `17.14.1`, xunit `2.9.3`, runner `3.1.4`, EF.Sqlite and Mvc.Testing `10.0.13` |

The student API additionally uses `Microsoft.Data.Sqlite.Core` `10.0.13` in DAL for typed recognition of the test provider's unique-constraint errors (no SQLite EF provider or native runtime is added to production). `StudentRepository` translates only the existing gym/email unique index into a DAL conflict, BLL translates it into `StudentEmailConflictException`, and the student-only HTTP branch returns a safe 409. Other persistence failures remain unexpected errors (generic 500); no schema or layer ownership changes are involved. See `src/Static_X.API/Students.http` for authenticated request samples.

DAL never references BLL/API; BLL never references API. API's DAL reference supports composition, not controller bypasses. BLL depends only on repository interfaces, not implementations or DbContext. `IActivationEmailSender` belongs to BLL and its Resend implementation belongs to API. Framework references add no new NuGet version; no existing framework or package was upgraded. AutoMapper is a separately authorized new BLL dependency. The existing Pkcs pin lives in DAL so the EF tooling graph does not resolve the older vulnerable transitive version for DAL/BLL. EF.Design is private in DAL and API because tools need both the target and startup projects.

Entity is **not entirely framework-free**: GymAccount's Identity inheritance is a deliberate user-selected exception. `Account` remains the pure domain account. No Entity → DAL project reference exists, even where a legacy namespace is named DAL. `ActivationDelivery` is a payload, not an EF-mapped entity. `ActivationOutbox` remains DAL-only. Configurations is reserved; the existing model builder was not decomposed.

## Compatibility choices

- BLL owns every specific HTTP/business request and response DTO in `DTOs/{Accounts,Activations,Gyms,Students}`, alongside `Interfaces` and `Services`. Each DTO is a separate conventional class with explicit properties and constructors in `BLL.DTOs.<Category>`. All applicable public business service contracts consume DTOs for input field groups and return DTOs rather than entities, including nested collections/results. BLL owns Entity-to-DTO conversion and business validation independently of MVC. API retains HTTP validation, status mapping, controllers, and framework `ProblemDetails`/validation envelopes, but never maps business entities. DataAnnotations on BLL DTOs do not move HTTP execution into BLL. DAL repositories remain entity-based and must never depend on BLL DTOs.
- Keep trusted gym/target identifiers, authorized roles, and cancellation separate from untrusted request DTOs; client gym/role fields never prove authorization. Login's existing `GymId` is only a credential selector and cannot authorize membership: repository authentication still validates the account in that gym. `SignInAsync` receives the selected gym separately and ignores DTO membership for scoping. Creation receives the authorized role separately from its credential DTO. Simple identifiers, enums, scalar outcomes, and cancellation-only operations need no artificial DTO: activation reissue retains gym/account identifiers, and dispatcher retains cancellation only. API Identity/auth/composition legitimately retains `GymAccount` and domain role enums; this exception does not permit controller business entity mapping.
- Service CLR contracts intentionally change: student add/update consume existing requests and every student result is `StudentResponse`; all account results are `AccountResponse`, create consumes `CreateAccountRequest`, and sign-in consumes existing `LoginRequest`; gym add consumes `CreateGymRequest` and returns `GymResponse`; activation provision consumes `ProvisionGymRequest` and activate consumes existing `ActivateAccountRequest`. `ProvisionGymRequest` is not Entity's persisted `ProvisioningRequest`. No endpoint, JSON field, numeric role serialization, CLI flag, or cookie behavior changes. `CsrfResponse` is still created by API from framework-generated transport data, not mapped by BLL.
- Moving the five named DTOs from API to BLL changes their CLR namespace/assembly, not their JSON shapes. Record DTOs become classes; no repository caller uses their generated value equality, deconstruction, or `with` support. Preserve property types, `required`/`init`, validation attributes, constructor argument names, and defaults. `CsrfResponse` replaces the anonymous public token response and explicitly retains lowercase `token`, including null. Domain entities (including `Account`), the Entity-owned `ActivationDelivery` repository payload, and the CLI's internal parser contract remain where they are. DAL must not reference BLL DTOs. Anonymous route values and DAL query projections are not public DTOs.
- Preserve existing CLR namespaces (`Entity`, `BLL`, `BLL.Services.Interfaces`, `BLL.Interfaces`, `DAL`, repository namespaces, and API namespaces) rather than combine a physical move with a wholesale public-name change. In particular, Entity-owned GymAccount/activation models retain their legacy CLR names, so snapshot entity names remain identical. Project names do not guarantee boundaries; regression checks inspect project references, assembly references, type ownership, and constructor dependencies.
- Rename only `dbcontext` to `DAL.StaticXDbContext`; update every options/store binding and designer/snapshot DbContext attribute. Rename the snapshot class/file accordingly. The physical reorganization preserved the five historical migration IDs and Up/Down bodies, model names, table/column/index mappings, and Identity adapter inheritance without adding a schema migration. The subsequent authorized [student state feature](Students.md) adds one migration and updates the snapshot. API explicitly selects the DAL migrations assembly; tools use DAL as target and API as startup.
- Preserve connection-string key `Static-X`, Data Protection application `Static-X.Activation`, protector purpose `Static-X.ActivationDelivery.v1`, per-invitation purpose, all CLI flags and exit behavior, cookies, claims, routes, status codes, and anti-CSRF behavior. Back up the same durable shared key ring; moving projects does not authorize rotating keys.
- WebApplicationFactory targets `Program` in `Static_X.API`; the existing Mvc.Testing build integration generates content-root metadata for its new project location. A regression check verifies both application assembly and `src/Static_X.API` content root. No hardcoded machine path is used.
- Existing uncommitted service/repository interface changes and failed-delivery-after-reissue protection are preserved. No service splitting or novel patterns are introduced.

The explicitly selected Development-only `dispatch-activation-local` CLI command uses the API-owned pickup adapter instead of Resend. Only its API composition supplies the dispatcher's trusted `requireResendConfiguration: false` constructor argument; it is not a configuration setting or an HTTP/API option. Existing dispatcher construction defaults to the unchanged Resend guards. Key-ring, HTTPS URL, protected outbox, invitation, Identity and CSRF rules remain intact. CLI and adapter enforce Development and explicit opt-ins independently; the HTTP host and normal dispatch never select pickup from those flags. No new layer, service contract, endpoint, migration or package is introduced. See [local activation operations](ActivationOperations.md#first-local-administrator-without-a-frontend-or-email-provider) for private external capture, database-isolation acknowledgement and manual HTTPS steps.

### Student listing contract extension

The authorized [student search and pagination](Students.md#list-response-and-query-rules) deliberately replaces the list's top-level array with `StudentListResponse` (`items`, `page`, `pageSize`, `totalCount`). `IStudentService.ListAsync` receives a BLL `StudentListRequest` plus a separate trusted gym identifier and cancellation. DAL retains scalar filters/bounds and returns its own `Repositories/Models/StudentPage`, containing Entity students and a filtered count; it does not depend on BLL DTOs. BLL maps the bounded items through `StudentProfile` and constructs page metadata directly. No additional layer, generic paging framework, mapping profile, schema change, or migration is introduced. Earlier JSON-preservation statements describe the physical migration, not this separately authorized HTTP contract change.

## AutoMapper response mapping

BLL owns `Mappings/StudentProfile`, `AccountProfile`, and `GymProfile`. Each maps one domain entity to its existing public response using explicit constructor parameters; DTO declarations and JSON shapes remain unchanged. `AccountProfile` maps pure `Account`, never the Identity adapter `GymAccount`. No reverse maps or request-to-entity maps are registered. Business validation, trimming, email normalization, authorization, and trusted gym/role selection stay in services. Student writes still construct entities explicitly with trusted gym context.

API `Program` calls `AddAutoMapper` once with the BLL `StudentProfile` marker, scanning only that assembly. Configuration is shared, not built per request; business services retain their scoped registrations. Only `StudentService`, `AccountService`, and `GymService` receive `IMapper`, and map repository-returned entities. Activation services and controllers do not receive it; DAL and Entity have no AutoMapper dependency. Tests validate configuration, constructor fields, null/collection behavior, persisted results, and the mapping boundary using real production profiles.

### Package and operator license setup

AutoMapper is pinned to `16.2.0` in BLL. The [NuGet manifest](https://api.nuget.org/v3-flatcontainer/automapper/16.2.0/automapper.nuspec) provides a native `net10.0` target and requires license acceptance; installation under the applicable terms was explicitly authorized. DI support is included in AutoMapper itself; do not add the obsolete DI extension package.

Installation authorization and passing tests are **not proof of production license compliance**. No license key is supplied by the repository, and no free/commercial eligibility is assumed.

1. The authorized operator must review the [applicable license](https://github.com/LuckyPennySoftware/AutoMapper/blob/dfa6dd587c5854b4beee5934beb39ba6e9569b84/LICENSE.md) and [official license configuration instructions](https://docs.automapper.io/en/stable/License-configuration.html), confirm the project's applicable licensing route, and obtain a valid key when required before production use. This implementation performs no purchase, signup, or license acquisition.
2. Supply the real key through the deployment secret store as configuration `AutoMapper:LicenseKey`; for environment-based configuration, use **`AutoMapper__LicenseKey`**. `Program` binds that setting to `configuration.LicenseKey`. Do not put the key in tracked files, local appsettings changes, tests, logs, or command history.
3. Restart the API process after configuring the environment-owned secret. Retain the `LuckyPennySoftware.AutoMapper.License` logger and review startup diagnostics in the restricted operator environment; never print the key to verify binding.

The official documentation states that missing keys produce warnings and invalid/expired keys produce errors, without disabling mapping or making outbound licensing calls. Tests use real mapper configurations without fabricated keys and do not suppress license diagnostics. Successful runtime mapping with a warning does not establish compliance. AutoMapper also documents fallback variables `AUTOMAPPER_LICENSE_KEY` and `LUCKYPENNY_LICENSE_KEY`; the explicit application setting takes precedence when supplied.

## Operational limits

Offline snapshot comparison and SQL generation validate model/history compatibility, not the state of any deployed database. Validate actual SQL Server deployment, persistent keys, scheduler permissions, and Resend delivery separately under the approved operational process. See [activation operations](ActivationOperations.md).

Old ignored build outputs or local IDE files may remain in the former directories; they are not active source projects. Moves use new parent paths and canonical casing without staging. On case-insensitive Windows, Git may still display original indexed casing until the eventual authorized staging step; do not stage merely to normalize the display.

The two existing machine-specific `appsettings*.json` files are moved intact and remain ignored at their new API paths. Do not stage them or expose their local configuration; deployment configuration remains environment-owned.
