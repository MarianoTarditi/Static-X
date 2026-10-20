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

- Precedence: explicit user decisions and this project skill override conflicting guide instructions. In particular, use **one project with logical API/BLL/DAL/ENTITY divisions**, not four required physical projects; define persistence contracts in BLL and implement them in DAL, not DAL-owned interfaces. Do not copy the contradictory structure or dependency table from guide lines 7, 23-24, or 49-58.
- `Static-X.slnx` currently lists four physical projects. Treat this as the existing scaffold, not the target architecture or authorization to reorganize it. Propose any migration separately; never restructure implicitly.
- Scope every account-owned read and write to its gym using trusted server-side identity/membership. Never accept a client-supplied gym ID alone as authorization; prevent cross-gym access, including indirect references and updates.
- AI may suggest routine changes; only a trainer can approve and apply them. Never auto-modify routines from AI output or treat suggestions as authorization.
- Keep ENTITY independent of transport and persistence details; BLL owns use cases, business authorization, and persistence contracts; DAL implements those contracts and owns storage; API owns HTTP contracts, authentication, policy enforcement, and composition. Keep dependencies acyclic and route functional requests through BLL.

## Decision Gates

| Situation | Action |
| --- | --- |
| Guide conflicts with selected layering | Follow the user-selected architecture above; flag the conflict when relevant. |
| Change requires physical project moves or reference rewiring | Ask for separate authorization before changing the scaffold. |

## Execution Steps

1. Inspect existing solution and relevant code before proposing changes; preserve unrelated structure.
2. Trace HTTP contract → BLL use case → BLL persistence contract → DAL implementation → ENTITY model as applicable.
3. Validate transport in API, business rules and gym ownership in BLL, and integrity in storage. Use explicit DTOs, parameterized queries, async I/O, cancellation where appropriate, and consistent error handling without leaking internals or secrets.
4. Test BLL rules, tenant isolation, trainer approval, and persistence constraints with a representative relational provider; run relevant build/tests when available.

## Output Contract

Report affected divisions, decisions or assumptions, executed checks, and any unverified behavior. Do not claim tests passed unless run.

## References

- [Full backend guide](../../../Docs/Guia_Backend_Senior_NET_Capas.md) — local source of detailed conventions, subject to the precedence above.
