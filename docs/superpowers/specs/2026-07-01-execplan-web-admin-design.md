# EXECPLAN Phase 2 — Web Admin Panel Design

> **Status:** Approved design (2026-07-01). Supersedes nothing; builds on the Phase 1 backend spine
> (`2026-06-30-execplan-backend-spine-design.md`, merged to `main` @ `34cc8f1`).
> **Scope:** The complete PRD §16 web admin panel — all 8 screens — in one Phase 2 increment.
> **Branch:** `feat/web-admin`.

---

## 1. Goal & Acceptance

Deliver the ASP.NET Core **MVC admin panel** that lets a non-technical Plan Manager, **entirely in Arabic (RTL)**,
create and activate an operational plan and watch its live dashboard — the PRD §21 "Web" acceptance row —
plus the System-Admin administration screens. The panel runs **in the same host process** as the existing
REST API and SignalR hub, and calls **Application services directly in-process** (never admin → HTTP → API;
CLAUDE.md invariant).

**Acceptance (must all hold):**

1. A manager signs in at `/admin/login` in Arabic, creates a plan through the 4-step wizard, activates it from
   Plan Detail, and sees the live dashboard counters update — all in-process, no `/api/v1` HTTP calls.
2. Language toggles ar⇄en, flipping `dir` and all labels; ar is the default.
3. Authorization matches PRD §14 exactly (see §5): Member has no web surface; Leader is scoped to own-team dashboards.
4. All assets (Bootstrap, IBM Plex Sans Arabic, SignalR client) are **self-hosted** — no external CDN (NFR-6).
5. Phase 1's 102 tests stay green; new MVC/SignalR integration tests cover the acceptance flow.

---

## 2. What Phase 1 already wired (do not rebuild)

From `src/ExecPlan.Api/Program.cs` as of `34cc8f1`:

- `AddControllersWithViews()` — Razor **view engine services are already registered** (Program.cs:30).
- Cookie scheme **`AuthPolicies.AdminCookieScheme` = `"AdminCookie"`** with `LoginPath = "/admin/login"`
  (Program.cs:99–103), commented "Reserved for the future MVC admin area." JWT bearer is the **default** scheme.
- `RequestLocalization`: default culture **`ar`**, supported **`ar`/`en`**, provider order cookie → query →
  Accept-Language (Program.cs:37–50). `AddLocalization()` present.
- Role policies `Admin` / `Manager` / `Leader` / `Member` / `ManagerOrAdmin` — **scheme-agnostic** `RequireRole`
  (Program.cs:105–113); they apply to a cookie principal that carries a `ClaimTypes.Role` claim.
- `ICurrentUser` → `CurrentUser` reads `IHttpContextAccessor.HttpContext.User` (scheme-agnostic); it reads
  `JwtRegisteredClaimNames.Sub` and `ClaimTypes.Role` (`src/ExecPlan.Api/Auth/CurrentUser.cs`).
- `IAuthService.ValidateCredentialsAsync(userName, password, ct)` returns `AppUserPrincipal(UserId, Role,
  FullName, UserName)` — **purpose-built for cookie sign-in** (`AuthService.cs:87`). Same constant-time path as
  API login (timing-safe against username enumeration).
- SignalR hub mapped at **`/hubs/dashboard`** (`[Authorize]`, `JoinActivation(Guid)` / `LeaveActivation(Guid)`),
  fed by `SignalRRealtimeNotifier`. The hub's `[Authorize]` accepts **both** JWT and cookie principals.
- `AppExceptionMiddleware` runs before auth (Program.cs:128), currently shaping every error as JSON.
- `DataSeeder` runs in Development / when `Seed:Enabled` — 5 demo users (admin/manager/leader/member/substitute),
  shared password `Passw0rd!` (DEC-19), one showcase plan.

**No package additions are required.** MVC, Razor, cookie auth, localization, and static files all ship in
`Microsoft.NET.Sdk.Web` (the `Microsoft.AspNetCore.App` shared framework). `ExecPlan.Api.csproj` targets `net9.0`.

**Phase 2 adds:** the `Areas/Admin` tree, a root `Views/Shared` layout set, `wwwroot/` + `app.UseStaticFiles()`,
the area route, the cookie sign-in/out actions, the localized `.resx` resources, the custom theme assets, the
SignalR JS client, and the `AppExceptionMiddleware` content-negotiation fix.

---

## 3. Architecture & topology

```
Browser (cookie, ar/en)
   │  HTML over /admin/*                        WS over /hubs/dashboard (cookie)
   ▼                                             ▼
Areas/Admin/Controllers : Controller  ──DI──▶  Application services  ──▶ Infrastructure (EF, SQL Server)
   │  (calls ValidateCredentials, Activation,          ▲                        │
   │   Dashboard, Escalation, Broadcast,               │                        ▼
   │   Execution, plus IUnitOfWork/IRepository         └── ICurrentUser (cookie principal) ── SignalRRealtimeNotifier
   │   for CRUD)                                                                 push
   ▼
Razor Views (_Layout RTL/LTR, IViewLocalizer, custom Material theme)
```

- Admin controllers live under **`src/ExecPlan.Api/Areas/Admin/`** and derive from `Controller`.
- Every admin controller is decorated
  `[Area("Admin")]` and
  `[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = …)]`
  — the explicit scheme is **mandatory**, because JWT is the default challenge scheme and a bare `[Authorize]`
  would return a 401 JSON challenge instead of redirecting to the cookie login.
- Controllers obtain data by calling Application services and `IUnitOfWork`/`IRepository<T>` **directly** through
  constructor injection. No `HttpClient`, no calls to `/api/v1`.
- For services that read the actor from `ICurrentUser` (`BroadcastService`, `ExecutionService`), the cookie
  principal supplies it automatically — no extra work, because `CurrentUser` is scheme-agnostic. For services
  that take an explicit `Guid actingUserId` (`ActivationService.ActivateAsync`,
  `AcknowledgeService.AcknowledgeAsync`), the controller passes `ICurrentUser.UserId`.

### 3.1 Program.cs changes (ordered)

1. Cookie options: extend the existing `.AddCookie(AdminCookieScheme, …)` with `AccessDeniedPath = "/admin/denied"`,
   `ExpireTimeSpan`, `SlidingExpiration = true`, `Cookie.HttpOnly = true`, `Cookie.SecurePolicy = Always`,
   `Cookie.SameSite = Lax`.
2. After `app.UseAuthorization()` add nothing new there; before `MapControllers()` add
   `app.UseStaticFiles();` (early, right after the exception middleware is fine) and, for the area,
   `app.MapControllerRoute("adminArea", "{area:exists}/{controller=Home}/{action=Index}/{id?}");`
   plus a root redirect `app.MapGet("/", ctx => { ctx.Response.Redirect("/admin"); return Task.CompletedTask; });`
   (attribute routing on API controllers is untouched).
3. `AppExceptionMiddleware` gains content negotiation (§4).

Nothing about JWT, the API controllers, the hub, or DI registration changes.

---

## 4. Error handling (AppExceptionMiddleware content negotiation)

The middleware currently converts `AppException` to a JSON problem for all requests. Change it to branch on the
request: a request is "API" if the path starts with `/api` **or** the `Accept` header prefers `application/json`;
otherwise it is an "admin/HTML" request.

- **API request:** unchanged — JSON problem with the status derived from `AppException.Kind`.
- **Admin/HTML request**, mapped from `AppException.Kind`:
  - `Unauthorized` → 302 redirect to `/admin/login?returnUrl=…`.
  - `Forbidden` → 302 redirect to `/admin/denied` (or render the AccessDenied view, 403).
  - `NotFound` → render `Views/Shared/NotFound.cshtml`, 404.
  - `Validation` / `Conflict` → render `Views/Shared/Error.cshtml` with the message, 400/409. (Form-level
    validation is preferred in the controller via `ModelState`; this is the fallback for service-thrown cases.)
- **Unhandled non-AppException** on an HTML request → generic `Error.cshtml` (500), no stack trace in the body.

The mapping table `Kind → (status, behavior)` lives in one place so API and HTML paths stay consistent.

---

## 5. Auth & authorization

### 5.1 Sign-in / sign-out

- **`GET /admin/login`** — the login form (`AllowAnonymous`). If already authenticated, redirect to the role
  landing page. Carries an optional `returnUrl`.
- **`POST /admin/login`** — validates the anti-forgery token, calls
  `IAuthService.ValidateCredentialsAsync(userName, password)`. On success:
  - Build a `ClaimsIdentity(AdminCookieScheme)` with **exactly the claims `CurrentUser` reads**:
    `JwtRegisteredClaimNames.Sub = UserId`, `ClaimTypes.Role = principal.Role.ToString()`,
    `JwtRegisteredClaimNames.Name = principal.FullName` (plus `ClaimTypes.NameIdentifier` for convenience).
  - **Reject `TeamMember`** here — a Member has no web surface (PRD §6); show a friendly "use the mobile app"
    message on the login page rather than signing them in.
  - `await HttpContext.SignInAsync(AdminCookieScheme, new ClaimsPrincipal(identity), authProps)`.
  - Redirect to `returnUrl` (local-only, guarded against open-redirect) or the role landing page.
  - On failure (`AppException.Unauthorized` from the service): re-render the form with a generic
    "invalid credentials" message (no username enumeration).
- **`POST /admin/logout`** — `SignOutAsync(AdminCookieScheme)` → redirect to login.
- **Role landing** is `HomeController.Index` at `/admin` (§7.9), which routes by role: SystemAdmin/PlanManager →
  My Plans; TeamLeader → their active-activations list. Member never reaches here.

### 5.2 Per-screen authorization (PRD §14, verbatim intent)

| Capability / screen | Admin | Manager | Leader | Member |
|---|---|---|---|---|
| Users / Departments / Organizations — **write** | ✓ | – | – | – |
| Users / Departments / Organizations — **read** | ✓ | ✓ (read-only) | – | – |
| Create / edit plans, wizard | ✓ | ✓ | – | – |
| Activate a plan | ✓ | if authorized activator | – | – |
| View live dashboard | ✓ (any) | ✓ (any) | own teams | – |
| Run escalation / broadcast / close | ✓ | ✓ | – | – |

Enforcement:

- **Controller/action policies** carry the coarse gate: admin-CRUD controllers use `Policy = Admin` on write
  actions and `Policy = ManagerOrAdmin` on read (`GET`) actions; plan + activation controllers use
  `Policy = ManagerOrAdmin`; the dashboard `GET` additionally allows `Leader` (policy that permits
  SystemAdmin/PlanManager/TeamLeader).
- **Record-level filtering** stays in the services (already built): `DashboardService`/the dashboard endpoint scope
  a TeamLeader to activations whose teams they lead (DEC-17); `ActivationService.ActivateAsync` enforces the
  authorized-activator rule; the hub group admits only Admin/Manager or a participating-team Leader (DEC-18).
  The MVC layer does **not** re-implement these — it calls the services and renders/handles their results and
  `AppException`s.

---

## 6. Localization & theme

### 6.1 Localization

- A marker `SharedResource` class in `src/ExecPlan.Api/Resources/` with `Resources/SharedResource.ar.resx` (the
  default/Arabic strings) and `SharedResource.en.resx`. Views inject `IHtmlLocalizer<SharedResource>` /
  `IViewLocalizer`; controllers that need strings inject `IStringLocalizer<SharedResource>`.
- `_ViewImports.cshtml` adds `@inject IViewLocalizer Localizer` (or a `@using` for the shared type) so views read
  `@Localizer["Key"]`.
- `_Layout.cshtml` sets `<html lang="@culture" dir="@(isRtl ? "rtl" : "ltr")">` from
  `CultureInfo.CurrentUICulture`; it links the RTL or LTR Bootstrap build accordingly.
- **Language toggle** — a header control posting to a small `LanguageController.Set(culture, returnUrl)` action
  that writes the `.AspNetCore.Culture` cookie (`CookieRequestCultureProvider.MakeCookieValue`) and redirects to a
  guarded local `returnUrl`. (Equivalent to the existing API `LocalizationController.SetLanguage`, but returns a
  redirect instead of JSON so it works from a plain form.)
- Every user-facing string lives in the `.resx` pair — no hard-coded Arabic or English in views (NFR-3, §15).
  The four wizard step names are fixed Arabic (§8.3): **معلومات الخطة · الفرق والأعضاء · المهام · النوبات والمراجعة**
  (with English counterparts in `en.resx`).

### 6.2 Theme (custom Material on Bootstrap 5 RTL)

- **Self-hosted assets** under `wwwroot/`:
  - `wwwroot/lib/bootstrap/` — Bootstrap 5 CSS (both `bootstrap.rtl.min.css` and `bootstrap.min.css`) + bundle JS.
  - `wwwroot/fonts/` — **IBM Plex Sans Arabic** woff2 (regular/medium/semibold) + `@font-face` in the theme CSS.
  - `wwwroot/lib/signalr/signalr.min.js` — the SignalR JS client (no CDN).
  - `wwwroot/css/execplan.css` — the theme: palette tokens, Material card elevation/shadows, large touch targets,
    one-primary-action layout, status colors.
  - `wwwroot/js/dashboard.js` — the live-dashboard client (§8.2).
- **Palette (PRD §16 design language):** ops-room **navy** chrome (top bar + sidebar); **amber** reserved
  exclusively for the «إطلاق الخطة» launch button; **green** for readiness/completion; **red** for
  escalation/overdue; neutral surfaces otherwise. One oversized primary action per screen; minimal text; big
  buttons (NFR-5, §4 principle 4).
- No external network dependency at runtime (NFR-6). Numerals follow existing repo convention (Latin digits are
  acceptable; do not add per-culture digit shaping unless the PRD requires it — it does not).

---

## 7. Screens (all 8 of PRD §16)

Each screen: route → controller/action → what it shows → the service/repository calls it makes → auth.

### 7.1 Login — `/admin/login`
Covered in §5.1. `AccountController` (Login GET/POST, Logout). Anonymous. Single centered card, one primary
"دخول" button, language toggle, demo-friendly.

### 7.2 Users administration — `/admin/users` (`UsersController`)
- **Index** (`GET`, ManagerOrAdmin): table of users (UserName, FullName, Role, Department, IsActive). Manager sees
  it **read-only** (no add/edit/deactivate controls rendered; write actions policy-gated to Admin).
- **Create** (`GET`/`POST`, Admin): add-user form (UserName, FullName, Phone, Role, Organization, Department,
  password). Hash via `IPasswordHasher`. Stage with `IUnitOfWork.Repo<User>().AddAsync` + one `SaveChangesAsync`.
- **Edit** (`GET`/`POST`, Admin): update FullName/Phone/Role/Department/IsActive. Deactivate = set `IsActive=false`
  (soft; never hard-delete an account that owns history).
- Uses `IRepository<User>`, `IRepository<Organization>`, `IRepository<Department>` for the dropdowns.

### 7.3 Departments & Organizations administration — `/admin/departments`, `/admin/organizations`
- `DepartmentsController` and `OrganizationsController`, each: Index (table) + Create (add form), Admin-write /
  Manager-read. Department create needs an Organization dropdown. **Organizations is added to satisfy FR-ADM-1**
  even though §16 lists only Departments — otherwise "create and list organizations and departments" is unmet.
- CRUD via `IRepository<Department>` / `IRepository<Organization>` + one `SaveChangesAsync` per write.

### 7.4 My Plans — `/admin/plans` (`PlansController.Index`)
- ManagerOrAdmin. Lists the acting manager's plans (Admin sees all) with a **status badge** (Draft = amber-outline,
  Ready = green). Each row links to Plan Detail; a prominent "خطة جديدة" button starts the wizard. A plan that
  currently has an **active** `PlanActivation` also shows a "watch dashboard" link to that activation — this is how
  a Manager re-enters a running dashboard after navigating away (no separate activations-index screen is needed).
- Query `IRepository<Plan>` filtered by `CreatedByUserId == ICurrentUser.UserId` for Manager; unfiltered for Admin.

### 7.5 Create-Plan Wizard — `/admin/plans/create/*` (§9 below)

### 7.6 Plan Detail — `/admin/plans/{id}` (`PlansController.Detail`)
- ManagerOrAdmin. Shows plan info, teams, members, tasks, roster (read view of the assembled template) and its
  status. If `Ready`, renders the **oversized amber «إطلاق الخطة»** button which opens a **plain-language
  confirmation** (modal or confirm page) before posting.
- **Activate** (`POST /admin/plans/{id}/activate`): calls `IActivationService.ActivateAsync(planId,
  ICurrentUser.UserId)`. On success redirect to the new activation's Live Dashboard. On `AppException.Forbidden`
  (not an authorized activator) → AccessDenied; on `Conflict`/`Validation` (e.g. not Ready, no roster) → back to
  Detail with the message.

### 7.7 Live Dashboard — `/admin/activations/{id}` (`ActivationsController.Dashboard`) — §8 below

### 7.8 Activation Summary — `/admin/activations/{id}/summary` (`ActivationsController.Summary`)
- ManagerOrAdmin. For a **closed** activation: final five counters, response/task-completion rates, per-team final
  ranking — from `DashboardService.GetSnapshotAsync` (the DTO carries `Status`/`Shift`/`RosterDate`, DEC-17), with
  **no live updates** and no action buttons (§16). If the activation is still Active, redirect to the live
  dashboard.

### 7.9 Home / role landing — `/admin` (`HomeController.Index`)
- Authenticated (any web role). Routes by role rather than being a screen of its own:
  SystemAdmin/PlanManager → redirect to My Plans; TeamLeader → a **thin active-activations list** (the activations
  covering the teams they lead, each linking to its Live Dashboard). This list reuses the same Leader scoping the
  dashboard read path enforces (DEC-17) and is the only web entry point a Leader has; it is not a new heavyweight
  screen. A Member never reaches here (rejected at login, §5.1).

---

## 8. Live Dashboard in depth

### 8.1 Initial render (server-side, works without JS)
`ActivationsController.Dashboard(id)` (GET; policy allows SystemAdmin/PlanManager/TeamLeader):
- Calls `IDashboardService.GetSnapshotAsync(activationId)` → `DashboardDto`.
- **Leader scoping:** the controller must not show a Leader an activation whose teams they don't lead. The dashboard
  read path already scopes this (DEC-17); the controller surfaces the resulting `AppException.Forbidden` as
  AccessDenied. (Manager/Admin see any activation.)
- Renders: the **five counters** `TotalParticipants / PendingCount / ReadyCount / EscalatedCount / InductedCount`
  (mutually exclusive, DEC-16), `ResponseRate`, `TaskCompletionRate`, the **team ranking** rows (sorted by `Score`
  desc — best-to-delayed, FR-MON-3), the **overdue** list, and the latest-50 **event feed** (`FeedEvent`
  Type/Text/AtUtc). Colour coding per palette (green ready, red escalated/overdue).
- Action controls (Manager/Admin only, hidden for Leader): **Run escalation**, **Broadcast** (opens a message
  modal), **Close** (confirmation). These render only when `Status == Active`.

### 8.2 Real-time (`wwwroot/js/dashboard.js`)
- Connect: `new signalR.HubConnectionBuilder().withUrl("/hubs/dashboard").withAutomaticReconnect().build()`
  — the cookie rides the negotiate/WS handshake automatically (same-origin), so no token wiring is needed for web.
- On start: `connection.invoke("JoinActivation", activationId)`.
- On the server push (`DashboardChangedAsync` fans out to the activation group), the client **re-fetches the JSON
  snapshot** from a small `GET /admin/activations/{id}/snapshot` action (returns the `DashboardDto` as JSON,
  ManagerOrAdmin+Leader, same scoping) and re-renders the counters/rates/ranking/overdue/feed in place.
  *(Re-fetch, rather than trusting a pushed payload, keeps the wire contract trivial and reuses the exact
  server-computed DTO.)*
- **Fallback (NFR-1):** if the hub is disconnected and reconnection fails, poll `…/snapshot` every ~5s until the
  hub recovers. On `ActivationClosedAsync` (or a snapshot with `Status == Closed`), stop polling, unsubscribe, and
  redirect to the Activation Summary.

### 8.3 Actions
- **Run escalation** — `POST /admin/activations/{id}/run-escalation` → `IEscalationService.RunCycleAsync` → the
  push refreshes the dashboard; show the `EscalationCycleResult` (attempts added / inducted) as a toast.
- **Broadcast** — `POST /admin/activations/{id}/broadcast` (body) → `BroadcastService.BroadcastAsync` (actor from
  `ICurrentUser`).
- **Close** — `POST /admin/activations/{id}/close` → `ExecutionService.CloseAsync` → redirect to Summary.
- All are anti-forgery-protected form posts; all map service `AppException`s through the middleware.

---

## 9. Create-Plan Wizard (4 steps, server-incremental draft)

**Persistence model — server-incremental draft (chosen).** Each step commits to the database against a `Draft`
plan; the wizard is resumable and refresh-safe. *Alternative rejected: accumulate all four steps in session and
save once — loses refresh-resilience and re-implements validation the services/entities already enforce.*

Steps and the fixed Arabic titles (§8.3 / FR-PLAN-6 — exactly four validated steps):

1. **معلومات الخطة (Plan info)** — `GET/POST /admin/plans/create` (or `…/create/info`).
   Fields: Name, Type (`PlanType`), Objective, Description, Scope; optionally emergency `PlanContact`s (name +
   number + `ContactKind`) and authorized `PlanActivator`s (users permitted to activate, FR-ACT-6). The **creator
   is implicitly an authorized activator**. POST creates the `Plan` with `Status = Draft`,
   `CreatedByUserId = ICurrentUser.UserId`; redirect to step 2 with `planId`. Server-side validation on required
   fields (Name and Type at minimum).
2. **الفرق والأعضاء (Teams & members)** — `GET/POST /admin/plans/create/{id}/teams`.
   Add one or more `Team`s (name, optional team-leader user) and their `TeamMembership`s, picking members from the
   **read-only user list** (FR-ADM-3/4). Uses `IRepository<Team>`, `IRepository<TeamMembership>`,
   `IRepository<User>`.
3. **المهام (Tasks)** — `GET/POST /admin/plans/create/{id}/tasks`.
   Add `TaskTemplate`s per team (Title, Order, Duration). At least one team must have at least one task
   (validated).
4. **النوبات والمراجعة (Shifts & review)** — `GET/POST /admin/plans/create/{id}/review`.
   Build the `ShiftAssignment` roster (per person: `ShiftBand`, `Date`, optional `SubstituteForUserId`), show a
   read-back review of the whole plan, then **Finish** → set `Status = Ready` and redirect to Plan Detail.
   Validation ensures the roster is non-empty and substitutes reference valid users.

Each step: renders the current draft state (so navigating back shows saved data), validates server-side, stages
with `IUnitOfWork` and one `SaveChangesAsync`. A wizard-navigation guard prevents jumping to a later step before
the earlier ones are satisfied. Guid PKs are ctor-assigned already (avoids the EF empty-Guid/AddRange traps).

---

## 10. File structure (new)

```
src/ExecPlan.Api/
  Program.cs                                  (modified: static files, area route, cookie opts, root redirect)
  Middleware/AppExceptionMiddleware.cs        (modified: HTML/JSON content negotiation)
  Resources/SharedResource.cs                 (marker)
  Resources/SharedResource.ar.resx
  Resources/SharedResource.en.resx
  Areas/Admin/
    Controllers/
      AccountController.cs                     (login GET/POST, logout)
      LanguageController.cs                    (set culture cookie + redirect)
      HomeController.cs                         (role landing / redirect)
      UsersController.cs
      DepartmentsController.cs
      OrganizationsController.cs
      PlansController.cs                        (Index, Detail, Activate)
      PlanWizardController.cs                   (the 4 steps)
      ActivationsController.cs                  (Dashboard, Snapshot(JSON), Summary, run-escalation, broadcast, close)
    Views/
      Account/Login.cshtml
      Users/{Index,Create,Edit}.cshtml
      Departments/{Index,Create}.cshtml
      Organizations/{Index,Create}.cshtml
      Plans/{Index,Detail}.cshtml
      PlanWizard/{Info,Teams,Tasks,Review}.cshtml
      Activations/{Dashboard,Summary}.cshtml
      _ViewImports.cshtml, _ViewStart.cshtml
    Models/ (view models: LoginVm, UserVm, PlanInfoVm, TeamsVm, TasksVm, ReviewVm, DashboardVm, …)
  Views/Shared/
    _Layout.cshtml, _LayoutHead.cshtml (assets), Error.cshtml, NotFound.cshtml, AccessDenied.cshtml
  Auth/
    ClaimsPrincipalFactory.cs                  (build the cookie identity from AppUserPrincipal — shared, tested)
  wwwroot/
    css/execplan.css
    js/dashboard.js
    fonts/…  lib/bootstrap/…  lib/signalr/signalr.min.js

tests/ExecPlan.IntegrationTests/Web/
  (new WebApplicationFactory-based MVC + SignalR tests — §11)
```

View models live in `Areas/Admin/Models`; controllers stay thin (validate → call service → view). No business
logic in views or controllers beyond mapping.

---

## 11. Testing

All new tests use the existing `WebApplicationFactory` harness (SQLite in-memory provider + `TestClock`), with a
`CookieContainer`/redirect-following `HttpClient` for the MVC flows.

1. **Auth & gating:** anonymous `/admin/*` → redirect to login; bad credentials re-render with generic error;
   Member login rejected; Admin/Manager land correctly; Leader restricted to own-team dashboards; each write action
   403s for the wrong role (Manager cannot create a user; Leader cannot open a foreign activation).
2. **Admin CRUD:** create/list/deactivate a user; create a department (with org) and an organization; Manager sees
   the user list read-only.
3. **Wizard:** drive all four steps to produce a `Ready` plan; assert the `Plan`, `Team`s, `TaskTemplate`s and
   `ShiftAssignment`s persisted; assert step-guard rejects skipping ahead; assert a refreshed draft reloads saved
   data.
4. **Activate + dashboard:** activate the plan; dashboard renders the five counters, rates, ranking, overdue, feed;
   run-escalation/broadcast/close behave; close redirects to Summary and Summary shows final counts with no action
   controls.
5. **Localization:** default response is `dir="rtl"` with Arabic labels; toggling to `en` sets `dir="ltr"` and the
   culture cookie; strings come from `.resx` (spot-check a key in both cultures).
6. **Error mapping:** a service `AppException.NotFound` on an HTML route renders the 404 view; `Forbidden` redirects
   to denied; the same kinds on `/api/*` still return JSON.
7. **Real-time:** a **cookie-authenticated** `HubConnection` to `/hubs/dashboard`, `JoinActivation`, then trigger an
   acknowledge → assert a `DashboardChanged` push arrives (mirrors the Phase 1 JWT hub test, but over the cookie
   principal).
8. **§21 web acceptance (end-to-end):** one test — manager signs in under `ar`, walks the wizard, activates, reads
   live counters — entirely in-process, asserting no outbound HTTP.

Phase 1's 102 tests must remain green; the suite target after Phase 2 is "all green, 0 warnings."

---

## 12. Out of scope (YAGNI)

- The full post-activation **FinalReport** / surveys / lessons-learned module — deferred by PRD §7.2 (closure shows
  counts only, which the Activation Summary already does).
- A **Settings** screen — the only tunable is the escalation threshold, which is config (`Escalation:DefaultThreshold`,
  FR-ESC-4); no UI is specified.
- **Mobile** (Flutter) — Phase 3.
- Multi-tenant org **scoping** of manager/admin authority — noted in DECISIONS "Open"; the panel does not add it,
  and does not regress it.

---

## 13. Decisions this design locks (to record as DEC-20…)

- **DEC-20** — Admin panel lives in an **`Areas/Admin`** MVC area inside `ExecPlan.Api`; controllers derive from
  `Controller` and are gated `[Authorize(AuthenticationSchemes = AdminCookie, Policy = …)]` (explicit scheme is
  required because JWT is the default challenge scheme).
- **DEC-21** — MVC controllers call **Application services + `IUnitOfWork`/`IRepository` directly in-process**; no
  admin → HTTP → API hop; cookie sign-in builds a principal whose claims match `CurrentUser` so `ICurrentUser` is
  scheme-agnostic.
- **DEC-22** — **Custom Material theme on self-hosted Bootstrap 5 RTL** (not a pre-built dashboard template),
  tuned to the PRD §16 design language; IBM Plex Sans Arabic + SignalR client bundled locally (NFR-6). Resolves the
  DEC-12 follow-up.
- **DEC-23** — Create-Plan Wizard uses a **server-incremental Draft** (each step commits; resumable), not
  session accumulation.
- **DEC-24** — `AppExceptionMiddleware` **content-negotiates**: JSON for `/api`/JSON-Accept, HTML
  redirects/views for admin requests, from one shared `Kind → (status, behavior)` mapping.
- **DEC-25** — An **Organizations** admin screen is added alongside Departments to satisfy FR-ADM-1 (which §16's
  screen list omitted).
- **DEC-26** — Live dashboard client **re-fetches a JSON snapshot** (`/admin/activations/{id}/snapshot`) on each
  hub push and **falls back to ~5s polling** when the hub is down (NFR-1); web hub auth is the same-origin cookie
  (no token wiring).
```
