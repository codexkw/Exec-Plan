# EXECPLAN — Locked Decisions Log

Living record of locked architecture/process decisions for the EXECPLAN re-platform (backend + MVC admin). Add a row when a decision is locked; **supersede explicitly** (never silently contradict). The design rationale lives in the design specs under `docs/superpowers/specs/`.

| # | Date | Decision | Choice | Rationale / source |
|---|---|---|---|---|
| DEC-1 | 2026-06-30 | Target framework | **.NET 9** | Installed SDK (9.0.305); matches portfolio. PRD's ".NET 10" = "latest LTS". |
| DEC-2 | 2026-06-30 | Host topology | **Single host** — one ASP.NET Core process hosts MVC admin + REST API + in-process SignalR hub | PRD §17.2; leanest ops (NFR-2). |
| DEC-3 | 2026-06-30 | Admin auth | **Cookie for admin, JWT (access+refresh, rotation) for mobile** | Server-rendered MVC is cookie-native; mobile uses JWT. Two schemes coexist. |
| DEC-4 | 2026-06-30 | First increment | **Backend spine** | Foundation web + mobile depend on; PRD §18.1. |
| DEC-5 | 2026-06-30 | Database | **SQL Server `83.229.86.221` / `Exec-Plan`** (dev + prod); **SQLite** = zero-dep eval/test mode | User direction; NFR-9. |
| DEC-6 | 2026-06-30 | Secrets | Real connection string / keys only in git-ignored `appsettings.Development.json` / user-secrets / prod env; committed files use placeholders | Never ship real creds in committed files. |
| DEC-7 | 2026-06-30 | Repos | Backend+MVC → `codexkw/Exec-Plan`; Flutter → `codexkw/Exec-Plan-Flutter` (separate) | User direction. |
| DEC-8 | 2026-06-30 | Keys | `Guid` PKs ctor-assigned (`Id = Guid.NewGuid()`); add children via repository, not tracked-parent nav | House style; avoids known EF traps. |
| DEC-9 | 2026-06-30 | Tests | xUnit + FluentAssertions; integration over SQLite in-memory | Fast; real relational semantics; SQL-Server-specific behavior smoke-tested later. |
| DEC-10 | 2026-06-30 | Workspace layout | Parent `ExecPlan/` is a plain container with two sibling repos: `backend/` + `mobile/` | User direction. |
| DEC-11 | 2026-06-30 | Admin localization | Admin ships **Arabic (default, RTL)** + **English (LTR)** via `RequestLocalization` + culture cookie + `.resx` | PRD §15; user direction. Host infra wired in Phase 1; views in web increment. |
| DEC-12 | 2026-06-30 | Admin UI theme | **Material Design admin template** (Bootstrap 5-based for RTL); candidate Creative Tim *Material Dashboard 2* (MIT). Assets bundled locally (NFR-6) | User direction. Exact template confirmed at start of web increment. |
| DEC-13 | 2026-06-30 | Refresh-token persistence boundary | `AuthService` (Application) never references the infra `RefreshToken` entity; it goes through `IRefreshTokenStore` (Application interface) implemented by `RefreshTokenStore` (Infrastructure), which stages rows on the shared UoW. | Preserves Domain←Application←Infrastructure; the plan's "AuthService consumes RefreshToken" would have violated it. |
| DEC-14 | 2026-06-30 | Async repository queries | `IRepository<T>` gains `FirstOrDefaultAsync(predicate)` and `ListAsync(predicate?)`; EF async lives in the Infrastructure impl. Services filter/read via these (no sync-over-async EF, no EF in Application). Dashboard loads each entity set by activationId and aggregates in memory (also sidesteps cartesian Includes). | Auth review flagged sync-over-async; every service needs async filtered reads. |
| DEC-15 | 2026-06-30 | Auth controller sequencing | `AuthController` (login/refresh) + a minimal `[Authorize]` diagnostic endpoint are built in the auth wave (with host wiring) so the JWT/cookie pipeline is testable; the CRUD controllers remain in the API wave. | Testability — host wiring needs a real protected endpoint and a login path to exercise. |
| DEC-16 | 2026-07-01 | Inducted-substitute status | An inducted substitute participant gets `ParticipantStatus.Inducted` (not `Pending`); lifecycle `Inducted → Ready` on acknowledge. The escalation cycle still processes only `Status==Pending`, so substitutes are not re-escalated in the MVP (they have no further substitute). | Makes the dashboard's five counters mutually exclusive (Total = Pending+Ready+Escalated+Inducted) and shows inducted-but-unconfirmed substitutes as a distinct category. |
| DEC-17 | 2026-07-01 | DashboardDto shape | `DashboardDto` carries `ActivationStatus Status`, `ShiftBand Shift`, `DateTime RosterDate` (restored — the close summary must show Closed and the payload needs shift context) plus the counters named `TotalParticipants/PendingCount/ReadyCount/EscalatedCount/InductedCount` (clearer than the plan's terse `Total/Pending/...`). Dashboard endpoint additionally scopes TeamLeader access to activations whose teams they lead (PRD §14 "own teams"); Manager/Admin see any. | Supersedes the plan's Locked-Contract DashboardDto shape (the T14 impl had silently dropped Status/Shift/RosterDate). Restores contract intent + records the naming. |

## Open (non-blocking)

- **Q-1** First real notification channel (WhatsApp Business vs local SMS gateway) — deferred; provider seam ready.
- **Q-3** Scheduler technology for timed escalation — deferred; CLI seam ready.
- **Q-5** Hub scale-out backplane (Redis vs SQL Server) — deferred; single-instance MVP.
- **DEC-12 follow-up** confirm exact Material template + license at start of the web increment.
