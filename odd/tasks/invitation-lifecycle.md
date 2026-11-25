# Invitation lifecycle
Backend delivery for tenant and gym administrator invitations; local checks with Static-X.sln.
Delivery: one coherent local slice; preserve existing worktree changes. Engram mirror pending workspace resolution.

## Specs

- S1 — «quiero que una vez que se envian las invitaciones, esta aparezca como pendiente» — The authorized listing reflects persisted queueing, not delivery; expose Pending, Expired and Cancelled and the next eligible resend time.
- S2 — «y se pueda volver a enviar (pasado cierto tiempo)», «1 hora» — A manual resend issues a replacement link after one hour from last queueing, including expired links; enforce consistently across existing and new paths and prevent concurrent duplicate queueing.
- S3 — «o cancelar invitacion» — Immediate cancellation invalidates the link and prevents pending dispatch without removing the account, changing the ficha or affecting activated accounts. An authorized fresh issuance to the same recipient is possible subject to cooldown.
- S4 — «si, vamos con ello» — Apply S1–S3 to Trainer, Student and GymAdmin invitations under the existing role and gym boundaries; backend only.

## Tasks

- [ ] T1 — S1–S4 — delegated direct; implemented and independently verified with 37 focused tests. Full suite and native review not closed.

## Log

- L1 — «quiero que una vez que se envian las invitaciones, esta aparezca como pendiente, y se pueda volver a enviar (pasado cierto tiempo)  o cancelar invitacion».
- L2 — «1 hora»; approved three-role backend proposal: «si, vamos con ello».
- L3 — Baseline is a dirty worktree; retain existing changes. Risk: item 2 (invitation authorization and credential invalidation), item 4 (atomic activation, resend and cancellation).
- L4 — Release build succeeded with no warnings or errors. Independent verification passed S1–S4 and 37 lifecycle tests; parent recheck also passed 37. Full suite: 912 passed, six Dashboard contract failures. Existing host locks Debug output. No migration or real mail was used.
