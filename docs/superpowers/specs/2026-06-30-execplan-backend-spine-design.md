# EXECPLAN — Phase 1 (Backend Spine) Design Spec

**Status:** Draft for review · **Date:** 2026-06-30 · **Source of truth for product requirements:** `PRD.md` (v1.1)

This spec governs the **first build increment** of the EXECPLAN re-platform: the **backend spine**. It scaffolds the full backend solution and fully implements the domain, persistence, services, auth, REST API, real-time hub wiring, CLI, seeding, and tests. The MVC admin **views** and the **Flutter** app are scaffolded/owned by later increments and separate work (Flutter is a separate repository).

It records the architectural decisions taken on 2026-06-30 and the concrete contracts an implementation plan will execute against. Where it restates product rules, the PRD remains authoritative.

---

## 1. Scope

### In scope (Phase 1)
- .NET 9 solution + clean-architecture project layout (Domain / Application / Infrastructure / Api / Cli + tests).
- The **15 PRD domain entities** + 1 infra-only `RefreshToken` table.
- EF Core persistence against **SQL Server** (with a SQLite provider switch for zero-dependency evaluation/tests).
- **AuthService** — username/password → JWT access+refresh with rotation; the same credentials back cookie (admin) and JWT (mobile) sign-in.
- **ActivationService, EscalationService, DashboardService, AcknowledgeService**, execution/task operations, broadcast, set-substitute-live, raise-issue — the full activation cycle, all transactional.
- **Kuwait shift/roster logic.**
- Provider seam (`INotificationProvider` + `DatabasePlaceholderProvider`) and real-time seam (`IRealtimeNotifier`).
- **REST `/api/v1/`** (JWT) per PRD §12, role-filtered, with object-level checks on task reassignment.
- **SignalR `DashboardHub`** wired in the host (JWT + cookie auth); REST snapshot endpoint as initial-load + fallback.
- **ExecPlan.Cli `run-escalation`** invoking the same `EscalationService`.
- **Seed data** for all four roles + a showcase plan, so the PRD §21 backend acceptance demo runs on first boot.
- **Tests** (xUnit) asserting the PRD §21 backend acceptance criteria.

### Out of scope (later increments / deferred per PRD §7.2)
MVC admin Razor views/controllers (host + auth wiring only in Phase 1); the Flutter app (separate repo `Exec-Plan-Flutter`); real notification channels; timed/scheduled escalation; shift handover; FinalReport/surveys; OS push (FCM/APNs); hub scale-out backplane.

---

## 2. Decisions log (resolved 2026-06-30)

| # | Decision | Choice | Rationale |
|---|---|---|---|
| DEC-1 | Target framework | **.NET 9** | Installed SDK (9.0.305); matches the rest of the portfolio. PRD's ".NET 10" read as "latest LTS". |
| DEC-2 | Host topology | **Single host** | One ASP.NET Core process hosts MVC admin + REST API + in-process SignalR hub. Matches PRD §17.2; leanest ops (NFR-2). |
| DEC-3 | Admin auth | **Cookie for admin, JWT for mobile** | Server-rendered MVC is cookie-native; mobile API uses JWT access+refresh. Two schemes coexist in the host. |
| DEC-4 | First increment | **Backend spine** | Foundation both web and mobile depend on; PRD §18.1 phase 1. |
| DEC-5 | Database | **SQL Server `83.229.86.221`, DB `Exec-Plan`** (already created), dev + prod. **SQLite** = zero-dep eval/test mode (NFR-9). | Per user direction. |
| DEC-6 | Secrets | Real `sa` connection string lives **only** in git-ignored `appsettings.Development.json` / user-secrets / prod env var. Committed files use placeholders. | Never ship real creds in committed files. |
| DEC-7 | Repos | Backend+MVC → `codexkw/Exec-Plan`. Flutter → `codexkw/Exec-Plan-Flutter` (separate). | Per user direction. |
| DEC-8 | Keys | `Guid` PKs assigned in entity constructor (`Id = Guid.NewGuid()`). | House style; avoids known EF traps (empty-Guid AddRange collision, tracked-parent collection-nav UPDATE-0-rows). |
| DEC-9 | Tests | xUnit + FluentAssertions; integration tests on SQLite in-memory (real relational semantics). | Fast, no SQL Server per test run; SQL-Server-specific behavior smoke-tested separately later. |
| DEC-10 | Workspace layout | Parent `ExecPlan/` is a plain container holding two sibling git repos: `backend/` (→ `Exec-Plan`) and `mobile/` (→ `Exec-Plan-Flutter`). | Per user direction 2026-06-30; keeps backend and Flutter as independent repos under one local folder. |
| DEC-11 | Admin localization | Admin panel ships **both Arabic (default, RTL) and English (LTR)** via ASP.NET Core `RequestLocalization` + culture cookie; `.resx` resource files, one key set per locale. | PRD §15 Arabic-first + complete English locale; explicit user direction 2026-06-30. |
| DEC-12 | Admin UI theme | MVC admin uses a **Material Design admin template** (Bootstrap 5-based for built-in RTL), candidate: Creative Tim *Material Dashboard 2* (MIT free) or equivalent. Assets bundled locally (NFR-6, no CDN). | Explicit user direction 2026-06-30. Exact template confirmed at the start of the web increment. |

> **Phasing note for DEC-11/DEC-12.** The localization *infrastructure* (RequestLocalization, supported cultures, culture-cookie endpoint, RTL-aware layout scaffolding) is wired in the Api host during Phase 1. The Material theme integration and the localized Razor **views** are built in the web increment (Phase 2). They are recorded here so the host is set up correctly from the start.

---

## 3. Solution topology

```
ExecPlan/                                     ← local container folder (NOT a repo)
├─ backend/   → repo codexkw/Exec-Plan        ← .NET 9 solution (this spec)
└─ mobile/    → repo codexkw/Exec-Plan-Flutter ← Flutter app (later increment)

backend/  (repo root = codexkw/Exec-Plan)
  ExecPlan.sln
  CLAUDE.md, PRD.md, docs/ (incl. DECISIONS.md, PROGRESS.md, this spec)
  src/
    ExecPlan.Domain          entities (15), enums, base types, invariants — no deps
    ExecPlan.Application      services, shift logic, DTOs, INotificationProvider,
                              IRealtimeNotifier, IClock, IUnitOfWork/repo abstractions — deps: Domain
    ExecPlan.Infrastructure   EF Core DbContext (SqlServer|Sqlite), migrations, cookie+JWT auth,
                              RefreshToken store, PasswordHasher, DatabasePlaceholderProvider,
                              KuwaitClock, seeding — deps: Application
    ExecPlan.Api              HOST: MVC (admin area, cookie) + REST /api/v1 (JWT) +
                              SignalR DashboardHub + SignalR-backed IRealtimeNotifier — deps: Infrastructure, Application
    ExecPlan.Cli              run-escalation → EscalationService via DI — deps: Infrastructure, Application
  tests/
    ExecPlan.UnitTests        pure logic (shift rules, threshold, ranking, guards)
    ExecPlan.IntegrationTests EF + services + API over SQLite in-memory

mobile/  (repo root = codexkw/Exec-Plan-Flutter)   ← separate; later increment
  CLAUDE.md, docs/ (DECISIONS.md, PROGRESS.md)
  Flutter app (member / leader / manager flows, SignalR client, ar/en)
```

**Dependency rule:** `Domain ← Application ← Infrastructure ← {Api, Cli}`. The Application project references **no EF Core and no SignalR** — it depends only on abstractions (`IUnitOfWork`, repositories, `INotificationProvider`, `IRealtimeNotifier`, `IClock`). This preserves the §17.3 invariant that one service layer drives identical behavior from API, CLI, and any future scheduler.

---

## 4. Domain model

Base type `BaseEntity { Guid Id (ctor-assigned); DateTime CreatedAtUtc; DateTime? UpdatedAtUtc }`. All timestamps stored **UTC**; shifts resolved against Asia/Kuwait explicitly (NFR-4).

### 4.1 Template side
- **Organization** — Name, parent of Departments/Users.
- **Department** — Name, OrganizationId.
- **User** — UserName (unique), PasswordHash, FullName, Phone, Role (`UserRole`), OrganizationId, DepartmentId, IsActive.
- **Plan** — Name, Type (`PlanType`), Objective, Description, Scope, Status (`PlanStatus`), CreatedByUserId. Owns: **PlanContact** (Name, Number, Kind = contact|emergency) child rows; **PlanActivator** (authorized activator UserIds) child rows.
- **Team** — PlanId, Name, TeamLeaderUserId (nullable).
- **TeamMembership** — TeamId, UserId.
- **ShiftAssignment** — TeamId, UserId, Shift (`ShiftBand`), Date (roster date, Kuwait calendar), `SubstituteForUserId` (nullable; non-null ⇒ this row designates a substitute for that user).
- **TaskTemplate** — TeamId, Title, Order, Duration (TimeSpan/minutes).

### 4.2 Runtime side (snapshot)
- **PlanActivation** — PlanId, Status (`ActivationStatus`), Shift (`ShiftBand`), RosterDate, ActivatedByUserId, ActivatedAtUtc, ClosedAtUtc (nullable), EscalationThreshold (copied from config at activation, default 5).
- **ActivationParticipant** — ActivationId, UserId (frozen), TeamId (frozen), TeamNameSnapshot (text), Status (`ParticipantStatus`: Pending|Ready|Escalated|Inducted), ResolvedSubstituteUserId (frozen at activation; leader may override live), CallAttemptCount, IsSubstitute (bool — inducted substitute), InductedFromParticipantId (nullable).
- **ExecutionTask** — ActivationId, ParticipantId (owner), Title (frozen from template), Order, Status (`TaskStatus`: Pending|Done), Note (nullable), DueAtUtc, CompletedAtUtc (nullable), SourceTaskTemplateId (provenance only, never read for frozen values).
- **NotificationLog** — ActivationId, RecipientUserId, Kind (notification|broadcast), Body, CreatedAtUtc.
- **CallAttempt** — ActivationId, ParticipantId, AttemptNumber, CreatedAtUtc.
- **ResponseStatus** — ActivationId, ParticipantId, AcknowledgedAtUtc. Presence = the one counted "responded".
- **EscalationLog** — ActivationId, ParticipantId (escalated), SubstituteUserId, NewParticipantId, CreatedAtUtc.
- **BroadcastMessage** — ActivationId, SenderUserId, Body, CreatedAtUtc.

### 4.3 Infra-only (outside the 15)
- **RefreshToken** — UserId, TokenHash, ExpiresAtUtc, CreatedAtUtc, RevokedAtUtc (nullable), ReplacedByTokenHash (nullable). Rotation store; not a domain concept.

### 4.4 Enums
- `UserRole` = SystemAdmin | PlanManager | TeamLeader | TeamMember
- `PlanType` = Daily | Weekly | Emergency | Guard | Transport | Maintenance | It | Inspection | General
- `PlanStatus` = Draft | Ready (template lifecycle)
- `ShiftBand` = Morning (06–14) | Evening (14–22) | Night (22–06)
- `ActivationStatus` = Active | Closed
- `ParticipantStatus` = Pending | Ready | Escalated | Inducted
- `TaskStatus` = Pending | Done

### 4.5 Synthesized event feed
No event table. The dashboard's chronological log is a union over NotificationLog, CallAttempt, ResponseStatus, EscalationLog, ExecutionTask completions, and BroadcastMessage, ordered by timestamp, latest 50 (PRD §11 note).

---

## 5. Application services

Services depend only on abstractions and complete writes in **one transaction** via `IUnitOfWork` (NFR-8). The notification provider **stages** rows; the calling service performs the single save.

### 5.1 AuthService
- `Login(userName, password)` → validates against `PasswordHash`; issues JWT access (short TTL) + refresh token (rotation row). Returns tokens + role for client routing (FR-AUTH-1/4).
- `Refresh(refreshToken)` → validates/rotates; revokes old, issues new pair (FR-AUTH-2). Invalid/expired → 401 (FR-AUTH-3).
- Admin cookie sign-in uses the same credential check; the cookie principal carries role claims.

### 5.2 Shift/roster logic — `KuwaitShiftCalculator` (pure, over `IClock`)
- Three fixed bands in Asia/Kuwait: Morning 06:00–14:00, Evening 14:00–22:00, Night 22:00–06:00.
- `Resolve(nowUtc)` → `ShiftResolution(ShiftBand, RosterDate)`. **Night-after-midnight rule:** a night shift in progress after 00:00 resolves to the **previous Kuwait calendar day's** roster.
- Heavily unit-tested at band boundaries and the midnight rollover.

### 5.3 ActivationService.Activate(planId, actingUserId)
Guards → reject if: plan already has an Active activation (FR-ACT-5); no one on duty for the resolved shift/date (FR-ACT-5); actor not creator/authorized-activator/admin (FR-ACT-6). Then, in one transaction:
1. Resolve (ShiftBand, RosterDate) via calculator (FR-ACT-2).
2. For each team's on-duty `ShiftAssignment` (non-substitute rows), create an `ActivationParticipant` with frozen UserId/TeamId/TeamNameSnapshot and **resolved substitute** frozen from the roster's substitute link.
3. For each participant, generate one `ExecutionTask` per `TaskTemplate` of their team, `DueAtUtc = ActivatedAtUtc + Duration` (FR-ACT-3).
4. Stage one `NotificationLog` + `CallAttempt #1` per participant (FR-ACT-4); set `CallAttemptCount = 1`.
5. Single `SaveChanges`. After commit: `IRealtimeNotifier.DashboardChanged(activationId)`.

### 5.4 EscalationService.RunCycle(activationId)
Identical behavior whether invoked from dashboard or CLI (FR-ESC-1). In one transaction:
- For each **Pending** participant: +1 `CallAttempt`, increment `CallAttemptCount` (FR-ESC-2).
- If `CallAttemptCount >= threshold` and still Pending: mark `Escalated`; **induct** the frozen substitute as a new `ActivationParticipant` (IsSubstitute, InductedFrom…) with full task set, `NotificationLog`, `CallAttempt #1`; write `EscalationLog` (FR-ESC-3). Threshold = activation's copied `EscalationThreshold` (config default 5, FR-ESC-4).
- After commit: real-time push.

### 5.5 DashboardService.GetSnapshot(activationId) → DashboardDto
Single server-side aggregate (FR-MON-1/2/3): five participant counters (Pending/Ready/Escalated/Inducted/total), response rate, task-completion rate, per-team rows **sorted best→delayed** (yielding best-team/delayed-team), overdue tasks (`DueAtUtc < now && Status==Pending`), latest 50 synthesized events. Uses `AsSplitQuery()` on multi-collection includes (avoids cartesian explosion). Returned by REST and pushed over the hub.

### 5.6 Execution / response / leader ops
- `Acknowledge(activationId, actingUserId)` → idempotent `ResponseStatus`; sets participant `Ready` (FR-EXE-1). Only counted response.
- `UpdateTask(taskId, done?, note?, reassignToParticipantId?, actingUser)` → mark done/undo + note (FR-EXE-2); **reassign** carries object-level checks: a leader may move a task only between participants of a team they lead (403 across team boundaries), and moving to a participant of a **different activation** is rejected (PRD §12, FR-EXE-4).
- `SetSubstituteLive(activationId, participantId, substituteUserId, actingUser)` → leader/manager updates `ResolvedSubstituteUserId` on a live activation, affecting later cycles (FR-EXE-5).
- `RaiseIssue(activationId, body, actingLeader)` → recorded, surfaced in the dashboard event log (FR-EXE-6, SHOULD).
- `Broadcast(activationId, body, actingManager)` → `BroadcastMessage` + per-participant `NotificationLog(kind=broadcast)`; one transaction; real-time push (FR-MON-4).
- `Close(activationId, actingManager)` → status Closed, ClosedAtUtc; returns final counts; clients unsubscribed from the hub (FR-MON-5).
- Visibility: members see only their own tasks and activations they participate in (FR-EXE-3), enforced by queryset filtering keyed on the acting user.

---

## 6. Infrastructure

- **EF Core** `ExecPlanDbContext`; provider chosen by config `Database:Provider` ∈ {`SqlServer`, `Sqlite`}. Code-first migrations. SQL Server is dev/prod; SQLite is the zero-dependency evaluation mode (NFR-9) and the test backend.
- **Connection string** (committed placeholder):
  `Server=83.229.86.221;Database=Exec-Plan;User Id=sa;Password=<DEV-SECRET>;TrustServerCertificate=True;Encrypt=True`
  Real password lives only in git-ignored `appsettings.Development.json` / user-secrets / prod env (`ConnectionStrings__Default`). **Never committed.**
- **Auth:** `PasswordHasher<User>` for hashing; cookie scheme `AdminCookie` (default for the `/admin` MVC area, redirect-to-login); JWT bearer for `/api/*`. Authorization policies per `UserRole`. `RefreshToken` rotation persisted.
- **`DatabasePlaceholderProvider : INotificationProvider`** stages NotificationLog/CallAttempt rows into the unit of work; one DI line swaps in a real channel later (NFR-7).
- **`KuwaitClock : IClock`** + `TimeZoneInfo` "Arab Standard Time"/IANA "Asia/Kuwait".
- **Seeding:** one Organization, a couple of Departments, one user per role (SystemAdmin, PlanManager, TeamLeader, TeamMember), and a showcase Plan with two teams, task templates, and a shift roster including a designated substitute — enough to run the §21 acceptance flow and the eventual drill demo. Seed is idempotent and dev/eval-gated.
- **Localization (DEC-11), host-level, wired in Phase 1:** `AddLocalization`; `RequestLocalizationOptions` with supported cultures `ar` (default) and `en`; providers ordered cookie → query → accept-language; a `SetLanguage` endpoint writing the `.AspNetCore.Culture` cookie. Layout sets `dir="rtl"` for Arabic / `ltr` for English from the request culture. Strings live in `.resx` resource files (one key set per locale); API validation/error messages are localizable too. The localized Razor views and the Material theme (DEC-12) are built in the web increment.

---

## 7. API surface (`/api/v1/`, JWT, role-filtered)

Per PRD §12:
- `POST /auth/login`, `POST /auth/refresh`
- CRUD: `organizations`, `departments`, `users`, `plans`, `teams`, `team-members`, `shift-assignments`, `task-templates`
- `POST /plans/{id}/activate`
- `GET /activations/{id}/dashboard`
- `POST /activations/{id}/acknowledge`
- `POST /activations/{id}/run-escalation`
- `PATCH /execution-tasks/{id}` (done / note / reassign — list + PATCH only)
- `POST /activations/{id}/broadcast`
- `POST /activations/{id}/set-substitute`
- `POST /activations/{id}/raise-issue`
- `GET /activations/{id}/my-tasks`, `GET /activations/{id}/my-notifications`
- `POST /activations/{id}/close`

Two-layer authorization (PRD §14): per-endpoint role gates **and** per-record queryset filtering. Reassignment object-level checks return 403 across team boundaries and reject cross-activation moves at validation.

---

## 8. Real-time — `DashboardHub`

- In-process SignalR hub in the Api host (NFR-1/2). Clients join the activation's group; on connect receive the full `DashboardDto`; on state change (readiness, task done/undo, escalation, induction, broadcast, close) the server pushes the recomputed slice (~1s).
- Authenticated by **both** JWT (mobile — token via `access_token` query string for the WS handshake) and cookie (web); group membership scoped by role so a client only receives activations it may see.
- REST `GET …/dashboard` is the initial-load path and the polling fallback if a persistent connection can't be established (resilience).
- `IRealtimeNotifier` (Application abstraction) is implemented here over `IHubContext<DashboardHub>`, keeping Application SignalR-free.

---

## 9. CLI

`ExecPlan.Cli run-escalation --activation <id>` and `--all-active` build a host, resolve `EscalationService` from the same DI container, and run identical logic to the dashboard trigger (FR-ESC-1). Seam for a future scheduler (Q-3) with zero logic duplication.

---

## 10. Configuration & secrets

- `appsettings.json` (committed): structure + placeholders, `Database:Provider`, `Escalation:DefaultThreshold=5`, JWT issuer/audience, cookie settings — **no secrets**.
- `appsettings.Development.json` (git-ignored): real connection string + JWT signing key for dev.
- Production: environment variables (`ConnectionStrings__Default`, `Jwt__SigningKey`, …).
- `.gitignore` excludes `appsettings.Development.json`, `appsettings.*.Local.json`, `bin/`, `obj/`, `.vs/`, user-secrets.

---

## 11. Testing strategy & acceptance mapping

xUnit + FluentAssertions. Unit tests for pure logic (shift bands + midnight roster rule, escalation threshold, team ranking order, reassignment guards). Integration tests over **SQLite in-memory** (kept-open connection) exercise EF + services + API end-to-end.

Tests assert the **PRD §21 backend acceptance criteria** directly:
- Log in as each role and route correctly.
- Activate the seeded plan → correct participants/tasks/notifications/first call.
- Acknowledge as a member → counted as the one response; member sees only own tasks.
- Run escalation → +1 attempt per pending; at threshold the substitute is inducted with full tasks/notification/call #1.
- `GET dashboard` returns correct counters, rates, ranking, overdue, and ≤50 events.

Caveat: SQLite/InMemory cannot reproduce every SQL-Server-specific behavior; a real-SQL-Server smoke path is deferred (not a Phase-1 blocker).

---

## 12. Git & repos

- `git init` the backend working tree (`Exec-Plan` repo, remote `https://github.com/codexkw/Exec-Plan.git`). Commit this spec and the scaffold. **No push until requested.**
- Flutter is a separate repo (`https://github.com/codexkw/Exec-Plan-Flutter.git`), scaffolded in a later increment.

---

## 13. Open questions (non-blocking for Phase 1)

- Q-1 first real notification channel — deferred (provider seam ready).
- Q-3 scheduler tech for timed escalation — deferred (CLI seam ready).
- Q-5 hub scale-out backplane — deferred (single-instance MVP).

---

## 14. Architectural invariants (must hold — PRD §17.3)

1. **One service layer** — API, CLI, future scheduler trigger identical behavior.
2. **Atomicity** — activation/escalation/broadcast each in a single transaction; provider stages, service saves once.
3. **Provider seam** — new channel = one class + one DI line; no change to activation/escalation/broadcast.
4. **Snapshot immutability** — a running activation never depends on mutable template structure.
5. **Single aggregated dashboard payload** — all aggregation server-side; pushed over the hub, same payload via REST for load/fallback.
