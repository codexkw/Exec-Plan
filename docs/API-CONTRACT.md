# EXECPLAN — REST + Realtime API Contract (mobile-facing)

> Source of truth for the **Flutter** client (`codexkw/Exec-Plan-Flutter`). Derived by reading the
> actual backend source (`src/ExecPlan.Api/Controllers`, `Hubs`, `Auth`, `Middleware`,
> `src/ExecPlan.Application`, `src/ExecPlan.Domain/Enums`) and independently re-verified by two
> adversarial audits on 2026-07-02. Where the wire format is surprising, the surprise is called out.
>
> **Nothing here requires the app to guess.** The five gaps in §8 are *known holes to be filled in
> Phase 3 Part A* — they are documented, not worked around.

Base URL is per-environment, supplied to the app via build config (`--dart-define=API_BASE_URL=…`).
All routes below are under that base. All timestamps are **UTC**; shift/roster logic is resolved
server-side against **Asia/Kuwait**.

---

## 1. Authentication

- **Scheme:** JWT Bearer is the default auth scheme. Send `Authorization: Bearer <accessToken>` on
  every non-anonymous call.
- **Token claims:** `sub` = userId (Guid), `name` = full name, `ClaimTypes.Role` = role **enum name**
  (e.g. `"TeamMember"`). `MapInboundClaims=false` — claims are read verbatim.
- **Login / refresh return `TokenPair`:**

  ```jsonc
  {
    "accessToken":     "…jwt…",
    "refreshToken":    "…",
    "accessExpiresUtc":"2026-07-02T12:00:00Z",
    "userId":          "guid",
    "role":            3,          // ⚠ INTEGER, not a name — see §3
    "fullName":        "…"
  }
  ```

  Use `accessExpiresUtc` for **proactive** refresh (refresh before it expires), not only reactive
  401 handling.

| Method | Route | Auth | Body → Response |
|---|---|---|---|
| POST | `/api/v1/auth/login` | anon | `{userName, password}` → `TokenPair` · **401 `{message}`** on bad creds |
| POST | `/api/v1/auth/refresh` | anon | `{refreshToken}` → `TokenPair` · **401 `{message}`** |
| GET  | `/api/v1/whoami` | any | → `{userId, role}` where **`role` is a STRING name** (the lone exception — see §3) |

⚠ **No logout / refresh-token revocation endpoint exists.** Logout = wipe local secure storage only.
Server-side session invalidation is a future security item, not in the current API.

---

## 2. Roles & authorization

`UserRole` (enum): `SystemAdmin=0, PlanManager=1, TeamLeader=2, TeamMember=3`.

- **SystemAdmin** — web admin only; not a mobile persona.
- **PlanManager** — mobile "mini-console" (activate/escalate/broadcast/close). No plan authoring on mobile.
- **TeamLeader** — dashboard for own teams + reassign/substitute + raise-issue; also a participant.
- **TeamMember** — readiness tap, own tasks, own notifications.

**Important:** several activation endpoints carry only a bare `[Authorize]` attribute but enforce
**object-level rules in the service layer**. The effective auth is listed per-endpoint in §4, not just
the attribute.

---

## 3. Enum wire encoding — READ THIS

There is **no `JsonStringEnumConverter` registered**, so `System.Text.Json` serializes every enum in a
response body as its **integer** value — *except* `whoami.role`, which is `.ToString()` (a string).
The client deserializer must map integers, and special-case `whoami`.

| Enum | Integer values |
|---|---|
| `UserRole` (`role`) | 0 SystemAdmin · 1 PlanManager · 2 TeamLeader · 3 TeamMember |
| `ActivationStatus` (`status`) | 0 Active · 1 Closed |
| `ShiftBand` (`shift`) | 0 Morning · 1 Evening · 2 Night |
| `ParticipantStatus` | 0 Pending · 1 Ready · 2 Escalated · 3 Inducted |
| `ExecTaskStatus` (`status`) | 0 Pending · 1 Done |
| `NotificationKind` (`kind`) | 0 Notification · 1 Broadcast |
| `PlanType` | 0 Daily · 1 Weekly · 2 Emergency · 3 Guard · 4 Transport · 5 Maintenance · 6 It · 7 Inspection · 8 General |
| `PlanStatus` | 0 Draft · 1 Ready |
| `ContactKind` | 0 Contact · 1 Emergency |

*(If Phase 3 D-2 opts to register the string converter server-side, these become names — but that is
gated on the web-admin regression suite. Default assumption: integers.)*

---

## 4. Endpoints

### 4.1 Activation lifecycle & live execution (`/api/v1/...`) — `{id}` = activation Guid

| Method | Route | Effective auth | Request → Response |
|---|---|---|---|
| GET | `activations/mine` | any authenticated | → `MyActivationListItemDto[]` — **the discovery entry point** (fills G1). Member/Leader: activations they're in (active + closed < 12h); Manager/Admin: all. Each row carries `myRole` + `myParticipantId?` |
| POST | `plans/{id}/activate` | Manager/Admin **+** creator/activator/admin check in service | → `{activationId}` · guards throw `Conflict`/`Forbidden` with codes `PlanAlreadyActive` / `NoOneOnDuty` / `NotAuthorizedToActivate` |
| GET | `activations/{id}/dashboard` | Admin/Manager (any) · **Leader** (own teams only, 403 otherwise) | → `DashboardDto` |
| GET | `activations/{id}/participants` | Admin/Manager · **Leader** (own teams only, 403 otherwise) · Members 403 | → `ParticipantRosterRowDto[]` — the roster with the `participantId` that set-substitute/reassign need (fills G5) |
| GET | `activations/{id}/teams/{teamId}/eligible-substitutes` | Admin/Manager · **Leader** of that team · Members 403 | → `SubstituteCandidateDto[] {userId, fullName}` — active team members not already on duty; feeds the set-substitute picker (fills G5b) |
| POST | `activations/{id}/acknowledge` | any authenticated | → **empty 200** — the **one counted «أنا جاهز» tap** (idempotent) |
| GET | `activations/{id}/my-tasks` | any | → `ExecutionTaskDto[]` (caller's own participant only; **200 + `[]` if not a participant**) |
| GET | `activations/{id}/my-notifications` | any | → `NotificationDto[]` (newest first) |
| PATCH | `execution-tasks/{id}` | owner / source-team leader / Manager-Admin (done+note); **reassign** = Manager-Admin OR a leader of **both** source & target teams | `{done?, note?, reassignToParticipantId?}` → **empty 200** |
| POST | `activations/{id}/raise-issue` | **TeamLeader ONLY** (Manager/Admin get 403 despite bare `[Authorize]`) | `{body}` → **empty 200** |
| POST | `activations/{id}/set-substitute` | Manager/Admin OR leader of the participant's **own** team | `{participantId, substituteUserId}` → **empty 200** |
| POST | `activations/{id}/run-escalation` | Manager/Admin | → `{attemptsAdded, inducted}` |
| POST | `activations/{id}/broadcast` | Manager/Admin | `{body}` → **empty 200** |
| POST | `activations/{id}/close` | Manager/Admin | → final `DashboardDto` |

⚠ **All mutations return an empty 200 body** (no echo of the changed entity). The client must
optimistically update and/or re-GET after each action. For members (poll-only, no hub) the
"acknowledge → settles to Ready" transition is **client-local** — there is no server echo and no push.

### 4.2 Reference data / CRUD — Manager/Admin only

`plans`, `users`, `departments`, `organizations`, `teams`, `team-members`, `task-templates`,
`shift-assignments` — each `GET / GET{id} / POST / PUT{id} / DELETE{id}` under `/api/v1/...`.
All are **class-gated `ManagerOrAdmin`** (users/dept/org **writes** are Admin-only). **A Member or
Leader token gets 403 on every one of these.**

⚠ `GET /api/v1/plans` returns **all plans, unfiltered** (not scoped to "plans I manage"). There is no
"managed-by-me" relationship in the API today.

### 4.3 Localization

`POST /api/v1/set-language?culture=ar|en` sets the `.AspNetCore.Culture` cookie — **cookie-based, not
useful for a stateless JWT mobile client.** The practical lever for the app is the **`Accept-Language`
request header** (the `AcceptLanguageHeaderRequestCultureProvider` is registered). `GET /api/v1/culture`
is a diagnostic.

---

## 5. SignalR — live dashboard

- **Hub URL:** `/hubs/dashboard`. JWT is passed on the **`?access_token=<jwt>` query string** (the
  WebSocket handshake can't send an `Authorization` header). Only paths under `/hubs` read it.
- **Client → server:** `JoinActivation(Guid)`, `LeaveActivation(Guid)`.
- **Server → client:** `DashboardUpdated(DashboardDto)`, `ActivationClosed(Guid)`.
- **Who may join:** Admin/Manager (any activation); **Leader** (own teams only). **TeamMembers are
  rejected at `JoinActivation` with a `HubException`** — a member's JWT *can* open the socket, but the
  join always fails. Members have **no realtime channel** (poll `my-notifications` instead).
- ⚠ **No initial snapshot on join.** `JoinActivation` only subscribes to the `act-{id}` group; the hub
  pushes `DashboardUpdated` **only on subsequent committed state changes**. The client must fetch
  `GET activations/{id}/dashboard` (REST) for the initial state, then apply deltas. Conveniently this
  is the same endpoint used for the polling fallback — one view-model, two sources, one initial path.

---

## 6. DTO shapes

- **`TokenPair`** — see §1.
- **`DashboardDto`**: `activationId, status, shift, rosterDate, totalParticipants, pendingCount,
  readyCount, escalatedCount, inductedCount, responseRate, taskCompletionRate, teams[], overdue[],
  events[]`. Invariant: `total = pending+ready+escalated+inducted`. Rates are doubles (ratios).
  - `TeamRow`: `teamId, teamName, members, readyCount, tasksTotal, tasksDone, score` (ordered best→delayed)
  - `OverdueTask`: `taskId, title, participantUserId, dueAtUtc` — ⚠ carries the **user** id, not a `participantId`
  - `FeedEvent`: `atUtc, type, text` — `text` is opaque English literal, newest-first, capped 50
- **`ExecutionTaskDto`**: `id, activationId, participantId, title, order, status, note, dueAtUtc, completedAtUtc`
- **`NotificationDto`**: `id, kind, body, createdAtUtc`
- **`MyActivationListItemDto`**: `activationId, planId, planName, status, shift, rosterDate, myRole ("Participant"|"Leader"|"Manager"), startedAtUtc, closedAtUtc?, myParticipantId?` — `planName` is a live read (no snapshot); `myParticipantId` is null for a pure manager.
- **`ParticipantRosterRowDto`**: `participantId, userId, fullName, teamId, teamName, status, isSubstitute, inductedFromParticipantId?, tasksTotal, tasksDone` — `teamName` is the frozen snapshot; `fullName` is a live read.
- **`SubstituteCandidateDto`**: `userId, fullName`

---

## 7. Error contract

The JSON error body is **not uniform** — the client decoder must tolerate all of these:

| Situation | Body | Status |
|---|---|---|
| A thrown `AppException` reaching the middleware | `{ "error": "<English>", "kind": "<NotFound\|Forbidden\|Unauthorized\|Conflict\|Validation>", "code": "<AppErrorCodes or null>" }` | 404/403/401/409/400 |
| `auth/login` / `auth/refresh` bad creds | `{ "message": "<English>", "code": "InvalidCredentials"\|"RefreshInvalid" }` | 401 |
| Bare `NotFound()` / `Unauthorized()` / `Forbid()`, framework JWT challenge, policy/role denial | **empty body** | 404/401/403 |
| Unhandled exception | `{ "error": "An unexpected error occurred.", "kind": "Internal", "code": null }` | 500 |

✅ **As of Phase 3 Part A the stable machine `code` IS emitted** (both the middleware JSON and the auth
401 body). Branch and **localize on `code`, never the English message**. The canonical values live in
`ExecPlan.Application.Common.AppErrorCodes` — this is the authoritative ARB/resx key list. User-facing codes
today: `PlanAlreadyActive`, `NoOneOnDuty`, `NotAuthorizedToActivate`, `AlreadyClosed`, `CrossTeamReassign`,
`RaiseIssueLeaderOnly`, `SetSubstituteForbidden`, `CloseManagerOnly`, `EscalateClosed`, `BroadcastEmpty`,
`BroadcastManagerOnly`, `InvalidCredentials`, `RefreshInvalid`. `code` is `null` on throws that carry none
(e.g. generic `NotFound`) and on the catch-all 500 — the client must tolerate a null/absent `code` and fall
back to `kind`. Empty-body 401/403 (framework challenge / policy denial) still carry no `code`.

---

## 8. Gap status (Phase 3 Part A)

All confirmed against source (2026-07-02). Four of six are now **FILLED** (backend Part A, 17 tests):

- **G1 — activation discovery.** ✅ **FILLED** — `GET /api/v1/activations/mine` (§4.1).
- **G2 — members get no realtime.** ⏸ **Deferred by decision** — members are poll-only for MVP
  (`my-notifications`), matching PRD §7.2 (in-app only, app open). Revisit post-MVP.
- **G3 — no device-push (FCM/APNs) registration.** ⏸ **Deferred** per PRD §7.2 — separate future phase
  (needs a device-registration endpoint + an `INotificationProvider` push channel).
- **G4 — error `code` absent.** ✅ **FILLED** — `code` now emitted on both error surfaces; catalogue in
  `AppErrorCodes` (§7).
- **G5 — no participant roster.** ✅ **FILLED** — `GET /api/v1/activations/{id}/participants` (§4.1).
- **G5b — no eligible-substitute list.** ✅ **FILLED** —
  `GET /api/v1/activations/{id}/teams/{teamId}/eligible-substitutes` (§4.1).

---
*Generated 2026-07-02 from backend `main`. Update this file when the API changes.*
