# CLAUDE.md — EXECPLAN Backend (`codexkw/Exec-Plan`)

Guidance for Claude Code (and any contributor) working in this repository.

## What this is

**EXECPLAN** (نظام إكسبلان) — an Arabic-first operational-plan **activation & live-execution tracking** system for Kuwait/GCC. One cycle: **Create Plan → Activate → Notify → Execute → Live Dashboard**. Two signature mechanics: the readiness tap «أنا جاهز» is the *only* counted response, and non-responders are auto-escalated to a frozen substitute after a threshold of call attempts.

- **Authoritative product requirements:** [`PRD.md`](PRD.md) (v1.1).
- **Authoritative design for the current build:** [`docs/superpowers/specs/2026-06-30-execplan-backend-spine-design.md`](docs/superpowers/specs/2026-06-30-execplan-backend-spine-design.md).
- **Locked decisions:** [`docs/DECISIONS.md`](docs/DECISIONS.md). **Progress log:** [`docs/PROGRESS.md`](docs/PROGRESS.md).

This repo is the **backend + MVC admin**. The Flutter mobile app is a **separate repo**: `codexkw/Exec-Plan-Flutter` (locally at `../mobile`).

## Stack & architecture

- **.NET 9**, single ASP.NET Core host = **MVC admin (cookie auth)** + **REST `/api/v1` (JWT access+refresh)** + **in-process SignalR `DashboardHub`**.
- **Clean architecture**, strict dependency direction: `Domain ← Application ← Infrastructure ← {Api, Cli}`.
- **`ExecPlan.Application` references NO EF Core and NO SignalR.** It talks only to abstractions: `IUnitOfWork`, repositories, `INotificationProvider`, `IRealtimeNotifier`, `IClock`. One service layer drives identical behavior from the API, the CLI, and any future scheduler.
- **MVC admin calls Application services directly in-process** — never admin → HTTP → API.
- **EF Core** + **SQL Server** (`Database:Provider=SqlServer`), with a **SQLite** switch for zero-dependency evaluation and tests.

## Non-negotiable conventions

1. **Secrets never get committed.** Real connection strings / signing keys live only in git-ignored `appsettings.Development.json`, user-secrets, or prod environment variables. Committed files and docs use placeholders. The `.gitignore` already blocks `appsettings.Development.json` etc. — keep it that way.
2. **Guid PKs assigned in the entity constructor** (`Id = Guid.NewGuid()`). Add child entities via `Repository<TChild>().AddAsync(child)`, **not** by mutating a tracked parent's collection nav (avoids EF UPDATE-0-rows and empty-Guid AddRange tracking collisions).
3. **Atomicity (NFR-8):** activation, escalation, and broadcast each complete in **one transaction**. The notification provider *stages* rows; the calling service performs the single `SaveChanges`.
4. **Snapshot immutability:** a running activation never depends on mutable template structure — runtime rows freeze names/values as text/ids at activation time.
5. **One counted response:** only `ResponseStatus` (the readiness tap) counts. Opening, viewing, or completing tasks never marks "responded".
6. **`AsSplitQuery()`** on any query with multiple collection `Include`s (avoids cartesian explosion / SQL timeouts).
7. **Arabic-first + RTL.** Admin ships **ar (default, RTL)** and **en (LTR)** via `RequestLocalization` + culture cookie; strings live in `.resx`. All shift logic and timestamps: store UTC, resolve against **Asia/Kuwait** (Morning 06–14 / Evening 14–22 / Night 22–06; night-after-midnight → previous day's roster).
8. **Provider seam (NFR-7):** adding a real notification channel (SMS/voice/WhatsApp) = one new `INotificationProvider` class + one DI line; no change to activation/escalation/broadcast.

## Working agreement — keep the living docs current (REQUIRED)

After **every** meaningful unit of work, before/with the commit that contains it:

- **`docs/PROGRESS.md`** — append a dated entry: what was finished, the commit short-SHA, and what's next. One row per completed task/milestone.
- **`docs/DECISIONS.md`** — when a decision is **locked** (or changed), add/update a `DEC-NN` row: decision, choice, rationale, date. Never silently contradict a locked decision — supersede it explicitly.
- **Design specs** under `docs/superpowers/specs/` remain the authoritative design; update them when the design itself changes (not for routine progress).
- Commit these doc updates **together with** the code they describe, so history stays self-explaining.

If a task is left incomplete, record the partial state and the next step in `docs/PROGRESS.md` so work can resume cleanly after a context reset.

## Build / test / run

```bash
dotnet build ExecPlan.sln
dotnet test                                   # xUnit + FluentAssertions (SQLite in-memory)
dotnet run --project src/ExecPlan.Api         # host: MVC admin + REST + SignalR
dotnet run --project src/ExecPlan.Cli -- run-escalation --activation <id>
dotnet ef migrations add <Name> -p src/ExecPlan.Infrastructure -s src/ExecPlan.Api
dotnet ef database update          -p src/ExecPlan.Infrastructure -s src/ExecPlan.Api
```

## Database

SQL Server `83.229.86.221`, database `Exec-Plan` (already created). The `sa` credentials are **not** in this repo — set `ConnectionStrings__Default` in `appsettings.Development.json` (git-ignored) or the environment. For a zero-dependency run, set `Database:Provider=Sqlite`.
