# EXECPLAN Phase 2 — Web Admin Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the ASP.NET Core MVC admin panel (all 8 PRD §16 screens) into the existing `ExecPlan.Api` host, so a non-technical Plan Manager can create, activate, and live-monitor an operational plan entirely in Arabic.

**Architecture:** A new `Areas/Admin` MVC area inside `ExecPlan.Api`; cookie-authenticated controllers deriving from `Controller` that call Application services + `IUnitOfWork`/`IRepository<T>` **directly in-process** (never admin → HTTP → API). Razor views over a custom Material theme on self-hosted Bootstrap 5 RTL; ar (default) / en localization via `RequestLocalization` + `.resx`. The live dashboard renders server-side and updates over the same-origin cookie-authenticated SignalR hub with a REST-snapshot poll fallback.

**Tech Stack:** .NET 9, ASP.NET Core MVC (Razor), cookie auth (`Microsoft.AspNetCore.Authentication.Cookies`, in the shared framework), `Microsoft.AspNetCore.SignalR` JS client (self-hosted), Bootstrap 5 RTL (self-hosted), IBM Plex Sans Arabic (self-hosted woff2), xUnit + FluentAssertions integration tests over `WebApplicationFactory` (SQLite in-memory). No new NuGet packages.

## Global Constraints

Every task's requirements implicitly include these (from the design spec, `CLAUDE.md`, and the PRD):

- **In-process only:** MVC controllers call Application services / `IUnitOfWork` / `IRepository<T>` via DI. **No `HttpClient`, no calls to `/api/v1`.**
- **Clean architecture unchanged:** do not add EF Core or SignalR references to `ExecPlan.Application` or `ExecPlan.Domain`. All new code lives in `ExecPlan.Api` (+ tests).
- **Explicit auth scheme:** every admin controller is `[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, …)]`. A bare `[Authorize]` defaults to JWT and returns a 401 JSON challenge instead of redirecting to the cookie login.
- **Claims contract:** the cookie identity carries `JwtRegisteredClaimNames.Sub = UserId`, `ClaimTypes.Role = Role.ToString()`, `JwtRegisteredClaimNames.Name = FullName` (+ `ClaimTypes.NameIdentifier = UserId`). These are exactly what `src/ExecPlan.Api/Auth/CurrentUser.cs` reads — do not invent other claim types.
- **Roles/authority (PRD §14):** user/department/organization **write = SystemAdmin only**, **read = SystemAdmin + PlanManager**; plans CRUD + wizard + activate = PlanManager/SystemAdmin; dashboard read = SystemAdmin/PlanManager (any) + TeamLeader (own teams); escalation/broadcast/close = PlanManager/SystemAdmin. **TeamMember has no web surface.** Record-level scoping (Leader→own teams, authorized-activator) is already enforced inside the services — do not re-implement it; call the service and handle its `AppException`.
- **Localization:** ar is the default culture (RTL); en is LTR. **Every user-facing string lives in `SharedResource.{ar,en}.resx`** — no hard-coded Arabic/English in views or controllers. `_Layout` sets `lang`/`dir` from `CultureInfo.CurrentUICulture`.
- **Self-hosted assets (NFR-6):** Bootstrap, IBM Plex Sans Arabic, and the SignalR client are committed under `wwwroot/` and served locally. **No CDN `<link>`/`<script>` at runtime.**
- **Design language (PRD §16):** ops-room **navy** chrome; **amber reserved only** for the «إطلاق الخطة» launch button; **green** = readiness/completion; **red** = escalation/overdue; one oversized primary action per screen; large touch targets; typeface IBM Plex Sans Arabic.
- **EF discipline:** Guid PKs are ctor-assigned already; add child rows via `Repository<TChild>().AddAsync(child)` (not by mutating a tracked parent collection); one `SaveChangesAsync` per write action (atomicity, NFR-8); `AsSplitQuery()` on any query with multiple collection `Include`s.
- **Secrets:** never commit real connection strings/keys. Nothing in this plan writes secrets to tracked files.
- **Regression bar:** Phase 1's 102 tests stay green; the suite ends at "all green, 0 warnings."

---

## Locked Contracts

These names/types/routes are fixed. Every task uses them verbatim.

### Existing symbols to reuse (do not redefine)
- `ExecPlan.Api.Auth.AuthPolicies` — constants: `AdminCookieScheme = "AdminCookie"`, `Admin`, `Manager`, `Leader`, `Member`, `ManagerOrAdmin`.
- `ExecPlan.Application.Auth.IAuthService.ValidateCredentialsAsync(string userName, string password, CancellationToken)` → `AppUserPrincipal(Guid UserId, UserRole Role, string FullName, string UserName)`.
- `ExecPlan.Application.Abstractions.ICurrentUser` — `Guid? UserId`, `UserRole? Role`, `bool IsInRole(UserRole)`.
- `ExecPlan.Application.Abstractions.IUnitOfWork` — `IRepository<T> Repo<T>()`, `Task<int> SaveChangesAsync(ct)`.
- `IRepository<T>` — `GetByIdAsync`, `Query()`, `Tracking()`, `FirstOrDefaultAsync(pred)`, `ListAsync(pred?)`, `FirstOrDefaultTrackedAsync`, `ListTrackedAsync`, `AddAsync`, `AddRangeAsync`, `Remove`.
- Services: `IActivationService.ActivateAsync(Guid planId, Guid actingUserId, ct)`; `IDashboardService.GetSnapshotAsync(Guid activationId, ct)` → `DashboardDto`; `IEscalationService.RunCycleAsync(Guid activationId, ct)` → `EscalationCycleResult(int AttemptsAdded, int Inducted)`; `AcknowledgeService.AcknowledgeAsync(Guid activationId, Guid actingUserId, ct)`; `ExecutionService.CloseAsync(Guid activationId, ct)` → `DashboardDto`, `.RaiseIssueAsync`, `.SetSubstituteLiveAsync`, `.UpdateTaskAsync`; `BroadcastService.BroadcastAsync(Guid activationId, string body, ct)`.
- `AppException` with `AppException.Kind { NotFound, Forbidden, Unauthorized, Conflict, Validation }` and `ErrorKind` property.
- `DashboardDto(Guid ActivationId, ActivationStatus Status, ShiftBand Shift, DateTime RosterDate, int TotalParticipants, int PendingCount, int ReadyCount, int EscalatedCount, int InductedCount, double ResponseRate, double TaskCompletionRate, IReadOnlyList<TeamRow> Teams, IReadOnlyList<OverdueTask> Overdue, IReadOnlyList<FeedEvent> Events)`. `TeamRow(Guid TeamId, string TeamName, int Members, int ReadyCount, int TasksTotal, int TasksDone, double Score)`. `OverdueTask(Guid TaskId, string Title, Guid ParticipantUserId, DateTime DueAtUtc)`. `FeedEvent(DateTime AtUtc, string Type, string Text)`.
- Entities/fields (verbatim): `Plan{Name, PlanType Type, Objective, Description, Scope, PlanStatus Status, Guid CreatedByUserId, List<PlanContact> Contacts, List<PlanActivator> Activators}`; `PlanContact{Guid PlanId, Name, Number, ContactKind Kind}`; `PlanActivator{Guid PlanId, Guid UserId}`; `Team{Guid PlanId, Name, Guid? TeamLeaderUserId}`; `TeamMembership{Guid TeamId, Guid UserId}`; `TaskTemplate{Guid TeamId, Title, int Order, TimeSpan Duration}`; `ShiftAssignment{Guid TeamId, Guid UserId, ShiftBand Shift, DateTime Date, Guid? SubstituteForUserId}`; `PlanActivation{Guid PlanId, ActivationStatus Status, ShiftBand Shift, DateTime RosterDate, Guid ActivatedByUserId, DateTime ActivatedAtUtc, DateTime? ClosedAtUtc, int EscalationThreshold}`; `User{UserName, PasswordHash, FullName, Phone, UserRole Role, Guid OrganizationId, Guid? DepartmentId, bool IsActive}`; `Organization{Name}`; `Department{Name, Guid OrganizationId}`.
- Enums: `UserRole{SystemAdmin=0,PlanManager=1,TeamLeader=2,TeamMember=3}`; `PlanType{Daily,Weekly,Emergency,Guard,Transport,Maintenance,It,Inspection,General}`; `PlanStatus{Draft=0,Ready=1}`; `ShiftBand{Morning=0,Evening=1,Night=2}`; `ActivationStatus{Active=0,Closed=1}`; `ContactKind{Contact=0,Emergency=1}`.
- `IPasswordHasher.Hash(string)` / `.Verify(hash, password)`.

### New symbols this plan introduces
- `ExecPlan.Api.Auth.AdminClaimsPrincipalFactory.Create(AppUserPrincipal p) : ClaimsPrincipal` (Task 3).
- `ExecPlan.Api.Areas.Admin.Controllers.*` — `AccountController`, `HomeController`, `LanguageController`, `UsersController`, `DepartmentsController`, `OrganizationsController`, `PlansController`, `PlanWizardController`, `ActivationsController`.
- `ExecPlan.Api.Areas.Admin.Models.*` — view models (fields listed per task).
- `ExecPlan.Api.Resources.SharedResource` (marker) + `Resources/SharedResource.ar.resx` / `.en.resx`.
- Test helpers `ExecPlan.IntegrationTests.Web.WebTestHelpers` (Task 3).

### Route table (area = `Admin`; all under `/admin`)
| Method | Path | Controller.Action | Auth |
|---|---|---|---|
| GET | `/` | (redirect) → `/admin` | — |
| GET | `/admin` | `Home.Index` | AdminCookie, authenticated |
| GET | `/admin/login` | `Account.Login` | AllowAnonymous |
| POST | `/admin/login` | `Account.Login` | AllowAnonymous |
| POST | `/admin/logout` | `Account.Logout` | AdminCookie, authenticated |
| GET | `/admin/denied` | `Account.Denied` | AllowAnonymous |
| POST | `/admin/language` | `Language.Set` | AllowAnonymous |
| GET | `/admin/users` | `Users.Index` | ManagerOrAdmin |
| GET/POST | `/admin/users/create` | `Users.Create` | Admin |
| GET/POST | `/admin/users/{id}/edit` | `Users.Edit` | Admin |
| GET | `/admin/departments` | `Departments.Index` | ManagerOrAdmin |
| GET/POST | `/admin/departments/create` | `Departments.Create` | Admin |
| GET | `/admin/organizations` | `Organizations.Index` | ManagerOrAdmin |
| GET/POST | `/admin/organizations/create` | `Organizations.Create` | Admin |
| GET | `/admin/plans` | `Plans.Index` | ManagerOrAdmin |
| GET | `/admin/plans/{id}` | `Plans.Detail` | ManagerOrAdmin |
| POST | `/admin/plans/{id}/activate` | `Plans.Activate` | ManagerOrAdmin |
| GET/POST | `/admin/plans/create` | `PlanWizard.Info` | ManagerOrAdmin |
| GET/POST | `/admin/plans/create/{id}/teams` | `PlanWizard.Teams` | ManagerOrAdmin |
| GET/POST | `/admin/plans/create/{id}/tasks` | `PlanWizard.Tasks` | ManagerOrAdmin |
| GET/POST | `/admin/plans/create/{id}/review` | `PlanWizard.Review` | ManagerOrAdmin |
| GET | `/admin/activations/{id}` | `Activations.Dashboard` | Roles SystemAdmin,PlanManager,TeamLeader |
| GET | `/admin/activations/{id}/snapshot` | `Activations.Snapshot` (JSON) | Roles SystemAdmin,PlanManager,TeamLeader |
| POST | `/admin/activations/{id}/run-escalation` | `Activations.RunEscalation` | ManagerOrAdmin |
| POST | `/admin/activations/{id}/broadcast` | `Activations.Broadcast` | ManagerOrAdmin |
| POST | `/admin/activations/{id}/close` | `Activations.Close` | ManagerOrAdmin |
| GET | `/admin/activations/{id}/summary` | `Activations.Summary` | ManagerOrAdmin |

Controllers use attribute routing: `[Area("Admin")]` + `[Route("admin/...")]` on the controller/actions (so paths are exact and independent of the conventional area route, which still exists as a fallback). All POSTs are `[ValidateAntiForgeryToken]`.

### resx key catalogue (define in Task 2; extend as noted per task)
Canonical keys (same key in both `.ar.resx` and `.en.resx`): `App.Title`, `Nav.Plans`, `Nav.Users`, `Nav.Departments`, `Nav.Organizations`, `Nav.Logout`, `Lang.Toggle`, `Login.Title`, `Login.UserName`, `Login.Password`, `Login.Submit`, `Login.Invalid`, `Login.MemberBlocked`, `Common.Save`, `Common.Cancel`, `Common.Create`, `Common.Edit`, `Common.Back`, `Common.Actions`, `Common.Yes`, `Common.No`, `Status.Draft`, `Status.Ready`, `Users.Title`, `Users.FullName`, `Users.Role`, `Users.Phone`, `Users.Active`, `Users.Add`, `Dept.Title`, `Dept.Name`, `Dept.Add`, `Org.Title`, `Org.Name`, `Org.Add`, `Plans.Title`, `Plans.New`, `Plans.Name`, `Plans.Type`, `Plans.Activate`, `Plans.ActivateConfirm`, `Wizard.Step1`, `Wizard.Step2`, `Wizard.Step3`, `Wizard.Step4`, `Wizard.Next`, `Wizard.Finish`, `Dash.Total`, `Dash.Pending`, `Dash.Ready`, `Dash.Escalated`, `Dash.Inducted`, `Dash.ResponseRate`, `Dash.TaskRate`, `Dash.Teams`, `Dash.Overdue`, `Dash.Events`, `Dash.Escalate`, `Dash.Broadcast`, `Dash.Close`, `Dash.CloseConfirm`, `Summary.Title`, `Error.NotFound`, `Error.Denied`, `Error.Generic`. The four wizard step names are fixed Arabic: `Wizard.Step1`=«معلومات الخطة», `Wizard.Step2`=«الفرق والأعضاء», `Wizard.Step3`=«المهام», `Wizard.Step4`=«النوبات والمراجعة».

---

## Task 1: Vendor self-hosted front-end assets + theme CSS + static-file serving

**Files:**
- Create: `src/ExecPlan.Api/wwwroot/lib/bootstrap/bootstrap.rtl.min.css`, `bootstrap.min.css`, `bootstrap.bundle.min.js`
- Create: `src/ExecPlan.Api/wwwroot/lib/signalr/signalr.min.js`
- Create: `src/ExecPlan.Api/wwwroot/fonts/IBMPlexSansArabic-Regular.woff2`, `-Medium.woff2`, `-SemiBold.woff2`
- Create: `src/ExecPlan.Api/wwwroot/css/execplan.css`
- Modify: `src/ExecPlan.Api/Program.cs` (add `app.UseStaticFiles();`)
- Test: `tests/ExecPlan.IntegrationTests/Web/StaticAssetsTests.cs`

**Interfaces:**
- Produces: the local asset URLs `/css/execplan.css`, `/lib/bootstrap/bootstrap.rtl.min.css`, `/lib/bootstrap/bootstrap.min.css`, `/lib/bootstrap/bootstrap.bundle.min.js`, `/lib/signalr/signalr.min.js`, `/fonts/IBMPlexSansArabic-*.woff2`, and the CSS design-token classes consumed by every later view: `.ep-navbar`, `.ep-sidebar`, `.ep-card`, `.btn-launch` (amber), `.badge-ready`/`.badge-escalated`/`.badge-overdue`, `.ep-primary` (oversized action).

- [ ] **Step 1: Vendor the assets (one-time download; they become committed local files).**

Run (PowerShell, from `src/ExecPlan.Api`):
```powershell
New-Item -ItemType Directory -Force wwwroot/lib/bootstrap, wwwroot/lib/signalr, wwwroot/fonts, wwwroot/css | Out-Null
$bs='5.3.3'
Invoke-WebRequest "https://cdn.jsdelivr.net/npm/bootstrap@$bs/dist/css/bootstrap.rtl.min.css" -OutFile wwwroot/lib/bootstrap/bootstrap.rtl.min.css
Invoke-WebRequest "https://cdn.jsdelivr.net/npm/bootstrap@$bs/dist/css/bootstrap.min.css"     -OutFile wwwroot/lib/bootstrap/bootstrap.min.css
Invoke-WebRequest "https://cdn.jsdelivr.net/npm/bootstrap@$bs/dist/js/bootstrap.bundle.min.js" -OutFile wwwroot/lib/bootstrap/bootstrap.bundle.min.js
Invoke-WebRequest "https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js" -OutFile wwwroot/lib/signalr/signalr.min.js
foreach ($w in 'Regular','Medium','SemiBold') {
  Invoke-WebRequest "https://cdn.jsdelivr.net/npm/@fontsource/ibm-plex-sans-arabic@5.0.20/files/ibm-plex-sans-arabic-arabic-400-normal.woff2" -OutFile "wwwroot/fonts/IBMPlexSansArabic-$w.woff2"
}
```
Note: the last loop is a placeholder for three weights — fetch the 400/500/600 Arabic-subset files from `@fontsource/ibm-plex-sans-arabic@5.0.20/files/ibm-plex-sans-arabic-arabic-{400,500,600}-normal.woff2` into `-Regular/-Medium/-SemiBold.woff2` respectively. These are committed; **no CDN is referenced at runtime** (NFR-6). If the environment has no network, obtain the same versioned files by any means and place them at these exact paths.

- [ ] **Step 2: Write `wwwroot/css/execplan.css` (theme tokens + components).**

```css
:root{
  --ep-navy:#0d2440; --ep-navy-2:#123157; --ep-amber:#f6a609; --ep-amber-d:#d98e00;
  --ep-green:#1f9d55; --ep-red:#d64545; --ep-ink:#1b2430; --ep-muted:#6b7686; --ep-surface:#f5f7fa;
}
@font-face{font-family:'IBM Plex Sans Arabic';font-weight:400;font-display:swap;src:url('/fonts/IBMPlexSansArabic-Regular.woff2') format('woff2');}
@font-face{font-family:'IBM Plex Sans Arabic';font-weight:500;font-display:swap;src:url('/fonts/IBMPlexSansArabic-Medium.woff2') format('woff2');}
@font-face{font-family:'IBM Plex Sans Arabic';font-weight:600;font-display:swap;src:url('/fonts/IBMPlexSansArabic-SemiBold.woff2') format('woff2');}
body{font-family:'IBM Plex Sans Arabic',system-ui,sans-serif;background:var(--ep-surface);color:var(--ep-ink);}
.ep-navbar{background:var(--ep-navy);color:#fff;}
.ep-sidebar{background:var(--ep-navy-2);color:#dbe4f0;min-height:calc(100vh - 56px);}
.ep-sidebar a{color:#dbe4f0;text-decoration:none;display:block;padding:.75rem 1rem;border-radius:.5rem;}
.ep-sidebar a:hover,.ep-sidebar a.active{background:rgba(255,255,255,.12);color:#fff;}
.ep-card{background:#fff;border:0;border-radius:.75rem;box-shadow:0 2px 8px rgba(13,36,64,.08);}
.ep-primary{font-size:1.15rem;padding:.9rem 1.6rem;border-radius:.6rem;min-height:52px;}
.btn-launch{background:var(--ep-amber);border-color:var(--ep-amber-d);color:#1b2430;font-weight:600;font-size:1.35rem;padding:1.1rem 2rem;min-height:64px;border-radius:.75rem;}
.btn-launch:hover{background:var(--ep-amber-d);}
.badge-ready{background:var(--ep-green);color:#fff;} .badge-escalated,.badge-overdue{background:var(--ep-red);color:#fff;}
.badge-draft{background:transparent;border:1px solid var(--ep-amber-d);color:var(--ep-amber-d);}
.counter-tile{border-radius:.75rem;padding:1rem;text-align:center;background:#fff;box-shadow:0 2px 8px rgba(13,36,64,.08);}
.counter-tile .n{font-size:2rem;font-weight:600;line-height:1;} .counter-tile.ready .n{color:var(--ep-green);} .counter-tile.escalated .n{color:var(--ep-red);}
[dir="rtl"] body{letter-spacing:normal;}
```

- [ ] **Step 3: Add static-file serving in `Program.cs`.** Insert immediately after `app.UseMiddleware<AppExceptionMiddleware>();`:
```csharp
app.UseStaticFiles();
```

- [ ] **Step 4: Write the failing test `tests/ExecPlan.IntegrationTests/Web/StaticAssetsTests.cs`.**
```csharp
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

public class StaticAssetsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public StaticAssetsTests(TestAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/css/execplan.css")]
    [InlineData("/lib/bootstrap/bootstrap.rtl.min.css")]
    [InlineData("/lib/bootstrap/bootstrap.min.css")]
    [InlineData("/lib/bootstrap/bootstrap.bundle.min.js")]
    [InlineData("/lib/signalr/signalr.min.js")]
    [InlineData("/fonts/IBMPlexSansArabic-Regular.woff2")]
    public async Task Static_asset_is_served_locally(string path)
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync(path);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```
Reuse the existing `TestAppFactory` (Phase 1 harness: SQLite in-memory + seed + `TestClock`). If it lives in a different namespace, add the correct `using`; do not create a second factory.

- [ ] **Step 5: Run the test — expect FAIL first (assets/UseStaticFiles absent), then PASS after Steps 1–3.**
Run: `dotnet test --filter FullyQualifiedName~StaticAssetsTests`
Expected: 6 passing.

- [ ] **Step 6: Commit.**
```bash
git add src/ExecPlan.Api/wwwroot src/ExecPlan.Api/Program.cs tests/ExecPlan.IntegrationTests/Web/StaticAssetsTests.cs
git commit -m "feat(web): vendor self-hosted assets, theme css, static-file serving"
```

---

## Task 2: MVC shell — area routing, shared layout, localization resources, login page (GET)

**Files:**
- Create: `src/ExecPlan.Api/Resources/SharedResource.cs`, `Resources/SharedResource.ar.resx`, `Resources/SharedResource.en.resx`
- Create: `src/ExecPlan.Api/Views/_ViewImports.cshtml`, `Views/_ViewStart.cshtml`, `Views/Shared/_Layout.cshtml`
- Create: `src/ExecPlan.Api/Areas/Admin/Views/_ViewImports.cshtml`, `Areas/Admin/Views/_ViewStart.cshtml`
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/AccountController.cs` (Login GET + Denied only, this task)
- Create: `src/ExecPlan.Api/Areas/Admin/Views/Account/Login.cshtml`
- Create: `src/ExecPlan.Api/Areas/Admin/Models/LoginVm.cs`
- Modify: `src/ExecPlan.Api/Program.cs` (view localization, area route, root redirect, cookie options)
- Test: `tests/ExecPlan.IntegrationTests/Web/ShellTests.cs`

**Interfaces:**
- Consumes: theme assets from Task 1.
- Produces: `LoginVm{ string? UserName, string? Password, string? ReturnUrl, string? Error }`; the `_Layout` (RTL/LTR, localized nav, language toggle form posting to `/admin/language`); `SharedResource` marker for `IViewLocalizer`/`IStringLocalizer`.

- [ ] **Step 1: `Program.cs` — enable view+data-annotations localization and register the area route + root redirect.**
Change `AddControllersWithViews()` (Program.cs:30) to:
```csharp
builder.Services.AddControllersWithViews()
    .AddViewLocalization()
    .AddDataAnnotationsLocalization(o =>
        o.DataAnnotationLocalizerProvider = (_, f) => f.Create(typeof(ExecPlan.Api.Resources.SharedResource)));
```
Extend the existing cookie registration (Program.cs:99–103) to:
```csharp
.AddCookie(AuthPolicies.AdminCookieScheme, options =>
{
    options.LoginPath = "/admin/login";
    options.AccessDeniedPath = "/admin/denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
```
After `app.MapControllers();` (Program.cs:136) add:
```csharp
app.MapControllerRoute("adminArea", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
app.MapGet("/", ctx => { ctx.Response.Redirect("/admin"); return Task.CompletedTask; });
```
(Attribute-routed controllers still win; this conventional route is a fallback for the area.)

- [ ] **Step 2: Create `Resources/SharedResource.cs`** — `namespace ExecPlan.Api.Resources; public sealed class SharedResource { }`. Create `SharedResource.ar.resx` and `SharedResource.en.resx` populated with **every key in the Locked Contracts catalogue** (ar = Arabic, en = English). Wizard step names in `.ar.resx` are the fixed Arabic strings.

- [ ] **Step 3: Root `Views/_ViewImports.cshtml`:**
```cshtml
@using ExecPlan.Api
@using ExecPlan.Api.Resources
@using Microsoft.AspNetCore.Mvc.Localization
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
@inject IViewLocalizer Localizer
```
Root `Views/_ViewStart.cshtml`: `@{ Layout = "_Layout"; }`. Duplicate both into `Areas/Admin/Views/` (the area needs its own `_ViewImports`/`_ViewStart`).

- [ ] **Step 4: `Views/Shared/_Layout.cshtml`** — sets culture/dir, links the RTL or LTR Bootstrap, the theme CSS, navbar + sidebar (localized), and the language toggle:
```cshtml
@using System.Globalization
@{
  var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
  var isRtl = culture == "ar";
  var bs = isRtl ? "/lib/bootstrap/bootstrap.rtl.min.css" : "/lib/bootstrap/bootstrap.min.css";
}
<!DOCTYPE html>
<html lang="@culture" dir="@(isRtl ? "rtl" : "ltr")">
<head>
  <meta charset="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/>
  <title>@Localizer["App.Title"]</title>
  <link rel="stylesheet" href="@bs"/>
  <link rel="stylesheet" href="/css/execplan.css"/>
</head>
<body>
  <nav class="ep-navbar navbar navbar-expand px-3 py-2">
    <span class="navbar-brand text-white">@Localizer["App.Title"]</span>
    <div class="ms-auto d-flex align-items-center gap-2">
      <form method="post" action="/admin/language">
        <input type="hidden" name="culture" value="@(isRtl ? "en" : "ar")"/>
        <input type="hidden" name="returnUrl" value="@Context.Request.Path@Context.Request.QueryString"/>
        <button class="btn btn-sm btn-outline-light" type="submit">@Localizer["Lang.Toggle"]</button>
      </form>
      @if (User?.Identity?.IsAuthenticated == true) {
        <form method="post" action="/admin/logout"><button class="btn btn-sm btn-outline-light">@Localizer["Nav.Logout"]</button></form>
      }
    </div>
  </nav>
  <div class="container-fluid"><div class="row">
    @if (User?.Identity?.IsAuthenticated == true) {
      <aside class="col-2 ep-sidebar p-2">
        <a href="/admin/plans">@Localizer["Nav.Plans"]</a>
        <a href="/admin/users">@Localizer["Nav.Users"]</a>
        <a href="/admin/departments">@Localizer["Nav.Departments"]</a>
        <a href="/admin/organizations">@Localizer["Nav.Organizations"]</a>
      </aside>
    }
    <main class="col p-4">@RenderBody()</main>
  </div></div>
  <script src="/lib/bootstrap/bootstrap.bundle.min.js"></script>
  @await RenderSectionAsync("scripts", required: false)
</body></html>
```

- [ ] **Step 5: `Areas/Admin/Models/LoginVm.cs`** — `namespace ExecPlan.Api.Areas.Admin.Models; public sealed class LoginVm { public string? UserName {get;set;} public string? Password {get;set;} public string? ReturnUrl {get;set;} public string? Error {get;set;} }`.

- [ ] **Step 6: `AccountController` (Login GET + Denied) and `Login.cshtml`.**
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ExecPlan.Api.Areas.Admin.Models;

namespace ExecPlan.Api.Areas.Admin.Controllers;

[Area("Admin")]
[AllowAnonymous]
[Route("admin")]
public sealed class AccountController : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true) return Redirect("/admin");
        return View(new LoginVm { ReturnUrl = returnUrl });
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();
}
```
`Areas/Admin/Views/Account/Login.cshtml` — centered card, single primary button, posts to `/admin/login` with anti-forgery:
```cshtml
@model ExecPlan.Api.Areas.Admin.Models.LoginVm
<div class="row justify-content-center"><div class="col-md-4">
  <div class="ep-card p-4 mt-5">
    <h1 class="h4 mb-3">@Localizer["Login.Title"]</h1>
    @if (!string.IsNullOrEmpty(Model.Error)) { <div class="alert alert-danger">@Localizer[Model.Error]</div> }
    <form method="post" action="/admin/login">
      @Html.AntiForgeryToken()
      <input type="hidden" name="ReturnUrl" value="@Model.ReturnUrl"/>
      <div class="mb-3"><label class="form-label">@Localizer["Login.UserName"]</label>
        <input class="form-control" name="UserName" autofocus required/></div>
      <div class="mb-3"><label class="form-label">@Localizer["Login.Password"]</label>
        <input class="form-control" type="password" name="Password" required/></div>
      <button class="btn btn-primary ep-primary w-100" type="submit">@Localizer["Login.Submit"]</button>
    </form>
  </div>
</div></div>
```
Add `Areas/Admin/Views/Account/Denied.cshtml`: an `ep-card` showing `@Localizer["Error.Denied"]` and a `Common.Back` link to `/admin`.

- [ ] **Step 7: Write the failing test `ShellTests.cs`.**
```csharp
using System.Net;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests.Web;

public class ShellTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ShellTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_redirects_to_admin()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin");
    }

    [Fact]
    public async Task Login_page_renders_rtl_arabic_by_default()
    {
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/admin/login");
        html.Should().Contain("dir=\"rtl\"").And.Contain("lang=\"ar\"");
        html.Should().Contain("execplan.css").And.Contain("bootstrap.rtl.min.css");
    }

    [Fact]
    public async Task Admin_root_requires_auth_redirects_to_login()
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/admin");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/login");
    }
}
```
`Admin_root_requires_auth` needs `HomeController` to exist and be `[Authorize(AdminCookie)]`. Add a minimal `HomeController` now (fleshed out in Task 3):
```csharp
[Area("Admin")][Route("admin")]
[Authorize(AuthenticationSchemes = ExecPlan.Api.Auth.AuthPolicies.AdminCookieScheme)]
public sealed class HomeController : Controller { [HttpGet("")] public IActionResult Index() => Redirect("/admin/plans"); }
```

- [ ] **Step 8: Run tests, expect PASS.** `dotnet test --filter FullyQualifiedName~ShellTests` → 3 passing. Then `dotnet build` → 0 warnings.

- [ ] **Step 9: Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/ShellTests.cs
git commit -m "feat(web): mvc shell — area routing, localized layout, login page"
```

---

## Task 3: Cookie sign-in — login POST, logout, role landing, Member rejection

**Files:**
- Create: `src/ExecPlan.Api/Auth/AdminClaimsPrincipalFactory.cs`
- Modify: `src/ExecPlan.Api/Areas/Admin/Controllers/AccountController.cs` (add Login POST, Logout)
- Modify: `src/ExecPlan.Api/Areas/Admin/Controllers/HomeController.cs` (role landing)
- Create: `tests/ExecPlan.IntegrationTests/Web/WebTestHelpers.cs`
- Test: `tests/ExecPlan.IntegrationTests/Web/AuthFlowTests.cs`

**Interfaces:**
- Consumes: `IAuthService.ValidateCredentialsAsync`, `AppUserPrincipal`, `AuthPolicies.AdminCookieScheme`, `LoginVm`.
- Produces: `AdminClaimsPrincipalFactory.Create(AppUserPrincipal) : ClaimsPrincipal`; `WebTestHelpers.LoginAsync(HttpClient, string user, string pass)` (returns the same client, now cookie-authenticated) and `WebTestHelpers.PostFormAsync(HttpClient, string url, IDictionary<string,string> fields)` (handles the anti-forgery token).

- [ ] **Step 1: `AdminClaimsPrincipalFactory`.**
```csharp
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using ExecPlan.Application.Auth;

namespace ExecPlan.Api.Auth;

public static class AdminClaimsPrincipalFactory
{
    public static ClaimsPrincipal Create(AppUserPrincipal p)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, p.UserId.ToString()),
            new(ClaimTypes.NameIdentifier, p.UserId.ToString()),
            new(ClaimTypes.Role, p.Role.ToString()),
            new(JwtRegisteredClaimNames.Name, p.FullName),
        };
        var id = new ClaimsIdentity(claims, AuthPolicies.AdminCookieScheme,
            JwtRegisteredClaimNames.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(id);
    }
}
```
(Claim types match `CurrentUser` — Sub + `ClaimTypes.Role`.)

- [ ] **Step 2: `AccountController` Login POST + Logout.**
```csharp
using Microsoft.AspNetCore.Authentication;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Enums;
using ExecPlan.Api.Auth;
// ... class members:
private readonly IAuthService _auth;
public AccountController(IAuthService auth) => _auth = auth;

[HttpPost("login")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Login(LoginVm vm, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(vm.UserName) || string.IsNullOrWhiteSpace(vm.Password))
    { vm.Error = "Login.Invalid"; return View(vm); }
    AppUserPrincipal principal;
    try { principal = await _auth.ValidateCredentialsAsync(vm.UserName!, vm.Password!, ct); }
    catch (AppException ex) when (ex.ErrorKind == AppException.Kind.Unauthorized)
    { vm.Error = "Login.Invalid"; return View(vm); }

    if (principal.Role == UserRole.TeamMember)   // no web surface
    { vm.Error = "Login.MemberBlocked"; return View(vm); }

    await HttpContext.SignInAsync(AuthPolicies.AdminCookieScheme,
        AdminClaimsPrincipalFactory.Create(principal),
        new AuthenticationProperties { IsPersistent = false });

    return LocalRedirect(SafeReturnUrl(vm.ReturnUrl) ?? "/admin");
}

[HttpPost("logout")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Logout()
{
    await HttpContext.SignOutAsync(AuthPolicies.AdminCookieScheme);
    return Redirect("/admin/login");
}

private string? SafeReturnUrl(string? url)
    => (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url)) ? url : null;   // open-redirect guard
```
(`Logout` needs `[AllowAnonymous]` removed for that action — since the controller is `[AllowAnonymous]`, add the explicit `[Authorize(...)]` on Logout as shown; ASP.NET honors the action-level attribute.)

- [ ] **Step 3: `HomeController` role landing.** Replace the Task-2 stub body:
```csharp
[HttpGet("")]
public IActionResult Index()
{
    if (User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager)))
        return Redirect("/admin/plans");
    if (User.IsInRole(nameof(UserRole.TeamLeader)))
        return Redirect("/admin/activations");   // thin leader landing (Task 14 adds the list view)
    return Redirect("/admin/login");
}
```
(For this task the Leader branch may 404 until Task 14; the tested behavior here is Admin/Manager → `/admin/plans` and unauthenticated → login.)

- [ ] **Step 4: `WebTestHelpers`.**
```csharp
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ExecPlan.IntegrationTests.Web;

public static class WebTestHelpers
{
    private static async Task<string> AntiForgeryTokenAsync(HttpClient c, string getUrl)
    {
        var html = await c.GetStringAsync(getUrl);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return m.Groups[1].Value;
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient c, string getUrl, string postUrl, IDictionary<string,string> fields)
    {
        var token = await AntiForgeryTokenAsync(c, getUrl);
        fields["__RequestVerificationToken"] = token;
        return await c.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    public static async Task LoginAsync(HttpClient c, string user, string pass)
    {
        var res = await PostFormAsync(c, "/admin/login", "/admin/login",
            new Dictionary<string,string> { ["UserName"]=user, ["Password"]=pass });
        // Cookie handler on the client persists the auth cookie for subsequent requests.
        if (res.StatusCode is not (System.Net.HttpStatusCode.Redirect or System.Net.HttpStatusCode.OK))
            throw new Exception($"login failed: {res.StatusCode}");
    }
}
```
Tests create the client with a cookie container: `_factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false })`.

- [ ] **Step 5: Write the failing test `AuthFlowTests.cs`.** Seeded accounts (DEC-19): `manager`,`admin`,`leader`,`member` — all password `Passw0rd!`.
```csharp
[Fact] public async Task Manager_logs_in_and_lands_on_plans() { /* LoginAsync(manager); GET /admin → 302 /admin/plans */ }
[Fact] public async Task Bad_credentials_re_render_with_generic_error() { /* wrong pass → 200 body contains Login.Invalid text, no auth cookie */ }
[Fact] public async Task Member_is_rejected_from_web() { /* login member → 200 body contains Login.MemberBlocked text; GET /admin/plans → 302 login */ }
[Fact] public async Task Logout_clears_cookie() { /* login manager; POST /admin/logout; GET /admin/plans → 302 login */ }
[Fact] public async Task Open_redirect_is_blocked() { /* login manager with ReturnUrl=https://evil.com → redirect is /admin, not evil */ }
```
Write these bodies fully using `WebTestHelpers`; assert on status codes and `Location`/body substrings (compare against the localized strings from `.ar.resx`).

- [ ] **Step 6: Run tests → PASS; `dotnet build` → 0 warnings.**
Run: `dotnet test --filter FullyQualifiedName~AuthFlowTests`

- [ ] **Step 7: Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/WebTestHelpers.cs tests/ExecPlan.IntegrationTests/Web/AuthFlowTests.cs
git commit -m "feat(web): cookie sign-in, logout, role landing, member rejection"
```

---

## Task 4: AppExceptionMiddleware content negotiation + shared error views

**Files:**
- Modify: `src/ExecPlan.Api/Middleware/AppExceptionMiddleware.cs`
- Create: `src/ExecPlan.Api/Views/Shared/Error.cshtml`, `Views/Shared/NotFound.cshtml` (AccessDenied handled by `Account/Denied` from Task 2)
- Test: `tests/ExecPlan.IntegrationTests/Web/ErrorMappingTests.cs` (+ a tiny test-only controller, see Step 3)

**Interfaces:**
- Consumes: `AppException`, `AppException.Kind`.
- Produces: HTML requests get redirects/views by `Kind`; `/api/*` and JSON-Accept requests keep JSON.

- [ ] **Step 1: Read the current middleware** at `src/ExecPlan.Api/Middleware/AppExceptionMiddleware.cs` to preserve its JSON mapping, then refactor the `catch (AppException ex)` block to branch on request kind.
```csharp
private static bool WantsJson(HttpContext ctx)
    => ctx.Request.Path.StartsWithSegments("/api")
       || (ctx.Request.Headers.Accept.ToString() is var a
           && a.Contains("application/json") && !a.Contains("text/html"));

// inside catch (AppException ex):
if (WantsJson(context)) { /* existing JSON problem response, unchanged */ return; }
switch (ex.ErrorKind)
{
    case AppException.Kind.Unauthorized:
        context.Response.Redirect($"/admin/login?returnUrl={Uri.EscapeDataString(context.Request.Path)}"); return;
    case AppException.Kind.Forbidden:
        context.Response.Redirect("/admin/denied"); return;
    case AppException.Kind.NotFound:
        await RenderViewResult(context, "NotFound", StatusCodes.Status404NotFound, ex.Message); return;
    default: // Validation / Conflict
        await RenderViewResult(context, "Error", StatusCodes.Status400BadRequest, ex.Message); return;
}
```
For HTML rendering from middleware, the simplest robust approach is a **redirect to dedicated GET endpoints** rather than rendering a view mid-pipeline. Implement `RenderViewResult` as: set status code, then `await context.Response.WriteAsync(...)` is *not* enough for a themed page — instead redirect to `/admin/error?code=404&msg=...` served by a small `ErrorController`. Add:
```csharp
// Areas/Admin/Controllers — or a root controller:
[AllowAnonymous][Route("admin")]
public sealed class ErrorPageController : Controller
{
    [HttpGet("notfound")] public IActionResult NotFound(string? msg) { Response.StatusCode = 404; ViewBag.Msg = msg; return View("NotFound"); }
    [HttpGet("error")]    public IActionResult Error(string? msg)    { Response.StatusCode = 400; ViewBag.Msg = msg; return View("Error"); }
}
```
and have the middleware redirect `NotFound`→`/admin/notfound`, `Validation/Conflict`→`/admin/error?msg=...`. Keep the JSON branch byte-for-byte as it is today so all Phase 1 API tests still pass.

- [ ] **Step 2: `Error.cshtml` / `NotFound.cshtml`** — each an `ep-card` showing `@Localizer["Error.NotFound"]` / `@Localizer["Error.Generic"]` and `@ViewBag.Msg`, with a `Common.Back` link to `/admin`.

- [ ] **Step 3: Test-only controller** in the test project (or a `#if DEBUG`-free minimal controller in Api guarded by an obviously test route) that throws each `AppException.Kind`, e.g. `GET /admin/_throw/{kind}`. Prefer adding it to the test project via the factory's `ConfigureServices`/`AddControllers().AddApplicationPart` — if that is heavy, add a tiny `ThrowController` under `Areas/Admin` gated to Development only (`if (!env.IsDevelopment()) return NotFound();`).

- [ ] **Step 4: Write `ErrorMappingTests.cs`.**
```csharp
[Fact] public async Task Html_notfound_renders_404_view() { /* GET html route that throws NotFound → 404, body contains Error.NotFound text */ }
[Fact] public async Task Html_forbidden_redirects_to_denied() { /* → 302 /admin/denied */ }
[Fact] public async Task Api_notfound_still_returns_json() { /* GET /api/... unknown id with Accept: application/json → JSON problem, not HTML */ }
```
For the API-JSON assertion, reuse an existing API 404 path (e.g. `GET /api/v1/plans/{randomGuid}` with a bearer token) so no new throw route is needed there.

- [ ] **Step 5: Run tests → PASS. Run the full suite to confirm no Phase 1 API test regressed:** `dotnet test`. Expected: all green (102 prior + new).

- [ ] **Step 6: Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/ErrorMappingTests.cs
git commit -m "feat(web): AppException content negotiation (html views vs api json)"
```

---

## Task 5: Language toggle

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/LanguageController.cs`
- Test: `tests/ExecPlan.IntegrationTests/Web/LocalizationTests.cs`

**Interfaces:**
- Consumes: `_Layout` toggle form (Task 2) posting `culture` + `returnUrl` to `/admin/language`.
- Produces: writes the `.AspNetCore.Culture` cookie and redirects to a guarded local `returnUrl`.

- [ ] **Step 1: `LanguageController`.**
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

[Area("Admin")][AllowAnonymous][Route("admin")]
public sealed class LanguageController : Controller
{
    [HttpPost("language")]
    [ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string? returnUrl)
    {
        var allowed = culture is "ar" or "en" ? culture : "ar";
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(allowed)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = false, IsEssential = true });
        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/admin/login");
    }
}
```
Note: the toggle form in `_Layout` must include `@Html.AntiForgeryToken()` — add it to the language form (and to the logout form) in `_Layout` now.

- [ ] **Step 2: Write `LocalizationTests.cs`.**
```csharp
[Fact] public async Task Default_is_arabic_rtl() { /* GET /admin/login → dir="rtl" lang="ar", Arabic Login.Title */ }
[Fact] public async Task Toggle_to_english_sets_ltr_and_cookie() {
  /* PostFormAsync culture=en,returnUrl=/admin/login → 302; follow → dir="ltr" lang="en", English Login.Title; response set a .AspNetCore.Culture cookie */ }
[Fact] public async Task Invalid_culture_falls_back_to_arabic() { /* culture=zz → next page still ar */ }
```

- [ ] **Step 3: Run tests → PASS.** `dotnet test --filter FullyQualifiedName~LocalizationTests`

- [ ] **Step 4: Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/LocalizationTests.cs
git commit -m "feat(web): language toggle (ar/en culture cookie)"
```

---

## Task 6: Users administration (Index / Create / Edit)

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/UsersController.cs`
- Create: `src/ExecPlan.Api/Areas/Admin/Models/UserListVm.cs`, `UserEditVm.cs`
- Create: `src/ExecPlan.Api/Areas/Admin/Views/Users/Index.cshtml`, `Create.cshtml`, `Edit.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/UsersAdminTests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`/`IRepository<User>`, `IRepository<Organization>`, `IRepository<Department>`, `IPasswordHasher`.
- Produces: `UserListVm{ IReadOnlyList<Row> Users; bool CanWrite }` where `Row{ Guid Id; string UserName; string FullName; UserRole Role; string? Department; bool IsActive }`; `UserEditVm{ Guid? Id; string UserName; string FullName; string? Phone; UserRole Role; Guid OrganizationId; Guid? DepartmentId; string? Password; bool IsActive; SelectList Orgs; SelectList Depts }`.

- [ ] **Step 1: `UsersController`** — Index (ManagerOrAdmin, `CanWrite = User.IsInRole(SystemAdmin)`), Create (Admin GET/POST), Edit (Admin GET/POST). Each write action ends with one `SaveChangesAsync`. Hash passwords via `IPasswordHasher`. Deactivate = set `IsActive=false` in Edit (never delete).
```csharp
[Area("Admin")][Route("admin/users")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class UsersController : Controller
{
    private readonly IUnitOfWork _uow; private readonly IPasswordHasher _hasher;
    public UsersController(IUnitOfWork uow, IPasswordHasher hasher){_uow=uow;_hasher=hasher;}

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var users = await _uow.Repo<User>().ListAsync(null, ct);
        var depts = await _uow.Repo<Department>().ListAsync(null, ct);
        var vm = new UserListVm { CanWrite = User.IsInRole(nameof(UserRole.SystemAdmin)),
            Users = users.Select(u => new UserListVm.Row(u.Id,u.UserName,u.FullName,u.Role,
                depts.FirstOrDefault(d=>d.Id==u.DepartmentId)?.Name, u.IsActive)).ToList() };
        return View(vm);
    }

    [HttpGet("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create(CancellationToken ct) => View(await BuildEditVm(null, ct));

    [HttpPost("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserEditVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.UserName) || string.IsNullOrWhiteSpace(vm.Password))
            ModelState.AddModelError("", "Users required fields");
        if (!ModelState.IsValid) { await FillLists(vm, ct); return View(vm); }
        var u = new User { UserName=vm.UserName, FullName=vm.FullName, Phone=vm.Phone, Role=vm.Role,
            OrganizationId=vm.OrganizationId, DepartmentId=vm.DepartmentId,
            PasswordHash=_hasher.Hash(vm.Password!), IsActive=true };
        await _uow.Repo<User>().AddAsync(u, ct);
        await _uow.SaveChangesAsync(ct);
        return Redirect("/admin/users");
    }
    // Edit GET/POST analogous: load by id, update FullName/Phone/Role/DepartmentId/IsActive,
    // set PasswordHash only if a new Password was supplied; one SaveChangesAsync.
    // BuildEditVm/FillLists populate SelectLists for Orgs and Depts.
}
```

- [ ] **Step 2: Views.** `Index.cshtml` renders an `ep-card` table (UserName, FullName, localized Role, Department, Active badge); if `Model.CanWrite`, show an `ep-primary` "Users.Add" link to `/admin/users/create` and per-row Edit links; **otherwise render neither** (Manager read-only). `Create.cshtml`/`Edit.cshtml` render the form fields bound to `UserEditVm` with `@Html.AntiForgeryToken()`, Role/Org/Dept as `<select>`, one primary Save button. Use localizer keys `Users.*`, `Common.*`.

- [ ] **Step 3: Write `UsersAdminTests.cs`.**
```csharp
[Fact] public async Task Admin_creates_user_persisted_and_hashed() {
  /* LoginAsync(admin); PostFormAsync create with UserName=t1,Password=Passw0rd!,Role=PlanManager,Org=<seeded>;
     then resolve IUnitOfWork in a scope: user exists, PasswordHash != "Passw0rd!", Verify() true */ }
[Fact] public async Task Manager_sees_readonly_list_no_add_link() {
  /* LoginAsync(manager); GET /admin/users → 200, body lacks /admin/users/create link */ }
[Fact] public async Task Manager_cannot_post_create() {
  /* LoginAsync(manager); POST create → 403 (or redirect to denied) */ }
[Fact] public async Task Deactivate_sets_isactive_false() {
  /* admin edits a seeded user IsActive=false → repo shows false */ }
```
To read state, resolve services from `_factory.Services.CreateScope()` and query `IUnitOfWork`.

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/UsersAdminTests.cs
git commit -m "feat(web): users administration (admin write, manager read-only)"
```

---

## Task 7: Departments + Organizations administration

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/DepartmentsController.cs`, `OrganizationsController.cs`
- Create: `Areas/Admin/Models/DeptVm.cs`, `OrgVm.cs`
- Create: `Areas/Admin/Views/Departments/Index.cshtml`,`Create.cshtml`; `Views/Organizations/Index.cshtml`,`Create.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/OrgDeptAdminTests.cs`

**Interfaces:**
- Consumes: `IRepository<Department>`, `IRepository<Organization>`.
- Produces: `OrgVm{ Guid? Id; string Name }`; `DeptVm{ Guid? Id; string Name; Guid OrganizationId; SelectList Orgs }`; both Index VMs expose `bool CanWrite` and a rows list.

- [ ] **Step 1:** Both controllers mirror the Users pattern: Index `[ManagerOrAdmin]` with `CanWrite = IsInRole(SystemAdmin)`; Create GET/POST `[Admin]`, one `SaveChangesAsync`. `OrganizationsController` route `admin/organizations`; `DepartmentsController` route `admin/departments` (Create needs an Organization `<select>`). **Organizations screen satisfies FR-ADM-1** (DEC-25).

- [ ] **Step 2:** Views mirror Users Index/Create (tables + add form, `CanWrite`-gated controls, localizer `Dept.*`/`Org.*`).

- [ ] **Step 3: Write `OrgDeptAdminTests.cs`.**
```csharp
[Fact] public async Task Admin_creates_organization() { /* persisted */ }
[Fact] public async Task Admin_creates_department_under_org() { /* persisted with OrganizationId */ }
[Fact] public async Task Manager_reads_but_cannot_write_dept() { /* GET 200 no add link; POST create → 403 */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/OrgDeptAdminTests.cs
git commit -m "feat(web): departments + organizations administration"
```

---

## Task 8: My Plans list + Plan Detail (read)

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/PlansController.cs` (Index + Detail this task; Activate in Task 13)
- Create: `Areas/Admin/Models/PlanListVm.cs`, `PlanDetailVm.cs`
- Create: `Areas/Admin/Views/Plans/Index.cshtml`, `Detail.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/PlansListDetailTests.cs`

**Interfaces:**
- Consumes: `IRepository<Plan>`, `IRepository<Team>`, `IRepository<TaskTemplate>`, `IRepository<ShiftAssignment>`, `IRepository<PlanActivation>`, `ICurrentUser`.
- Produces: `PlanListVm{ IReadOnlyList<Row> Plans }` `Row{ Guid Id; string Name; PlanType Type; PlanStatus Status; Guid? ActiveActivationId }`; `PlanDetailVm{ Guid Id; string Name; PlanType Type; PlanStatus Status; string? Objective; string? Description; string? Scope; IReadOnlyList<TeamBlock> Teams; Guid? ActiveActivationId }` where `TeamBlock{ string Name; IReadOnlyList<string> Members; IReadOnlyList<string> Tasks }`.

- [ ] **Step 1: `PlansController` Index.** ManagerOrAdmin. Manager sees `CreatedByUserId == ICurrentUser.UserId`; Admin sees all. Compute `ActiveActivationId` = the id of a `PlanActivation` for that plan with `Status==Active` (if any) → drives the "watch dashboard" link (spec §7.4). Status badge: Draft→`badge-draft`, Ready→`badge-ready`.

- [ ] **Step 2: `PlansController` Detail.** ManagerOrAdmin. Load the plan; assemble `TeamBlock`s from Teams + memberships + task templates (use `AsSplitQuery()` if you `Include` multiple collections; otherwise separate `ListAsync` calls per collection). If plan not found → throw `AppException.NotFound` (middleware renders 404). Include `ActiveActivationId`.

- [ ] **Step 3: Views.** `Index.cshtml`: `ep-card` list, each row Name + localized Type + status badge, link to `/admin/plans/{id}`; if `ActiveActivationId` present, a secondary link to `/admin/activations/{id}`; a top `ep-primary` "Plans.New" button → `/admin/plans/create`. `Detail.cshtml`: plan info + teams/members/tasks read-back. (The oversized Activate button is added in Task 13; leave a clearly marked placeholder region `<!-- activate: Task 13 -->` — this is the one exception where a later task fills a named region.)

- [ ] **Step 4: Write `PlansListDetailTests.cs`.** Arrange a plan via a service scope (create `Plan` with `CreatedByUserId = manager`, a team, a task) then:
```csharp
[Fact] public async Task Manager_sees_only_own_plans_with_badges() { }
[Fact] public async Task Admin_sees_all_plans() { }
[Fact] public async Task Detail_renders_teams_and_tasks() { }
[Fact] public async Task Missing_plan_returns_404_view() { }
```

- [ ] **Step 5: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/PlansListDetailTests.cs
git commit -m "feat(web): my plans list + plan detail (read)"
```

---

## Task 9: Create-Plan Wizard — Step 1 (Plan info → Draft)

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/PlanWizardController.cs` (Info action)
- Create: `Areas/Admin/Models/WizardInfoVm.cs`
- Create: `Areas/Admin/Views/PlanWizard/Info.cshtml`, `Areas/Admin/Views/PlanWizard/_Steps.cshtml` (shared step header partial)
- Test: `tests/ExecPlan.IntegrationTests/Web/WizardStep1Tests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`, `ICurrentUser`.
- Produces: on POST creates a `Plan{Status=Draft, CreatedByUserId}` and redirects to `/admin/plans/create/{id}/teams`. `WizardInfoVm{ string Name; PlanType Type; string? Objective; string? Description; string? Scope }`. `_Steps.cshtml` renders the 4 localized step names (`Wizard.Step1..4`) with the active one highlighted (partial takes an `int` active step via `ViewData["step"]`).

- [ ] **Step 1: `PlanWizardController` Info GET/POST.**
```csharp
[Area("Admin")][Route("admin/plans/create")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class PlanWizardController : Controller
{
    private readonly IUnitOfWork _uow; private readonly ICurrentUser _me;
    public PlanWizardController(IUnitOfWork uow, ICurrentUser me){_uow=uow;_me=me;}

    [HttpGet("")] public IActionResult Info() { ViewData["step"]=1; return View(new WizardInfoVm()); }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Info(WizardInfoVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Name)) ModelState.AddModelError(nameof(vm.Name), "required");
        if (!ModelState.IsValid) { ViewData["step"]=1; return View(vm); }
        var plan = new Plan { Name=vm.Name, Type=vm.Type, Objective=vm.Objective,
            Description=vm.Description, Scope=vm.Scope, Status=PlanStatus.Draft,
            CreatedByUserId=_me.UserId!.Value };
        // creator is an implicit authorized activator:
        await _uow.Repo<Plan>().AddAsync(plan, ct);
        await _uow.Repo<PlanActivator>().AddAsync(new PlanActivator{ PlanId=plan.Id, UserId=_me.UserId!.Value }, ct);
        await _uow.SaveChangesAsync(ct);
        return Redirect($"/admin/plans/create/{plan.Id}/teams");
    }
}
```

- [ ] **Step 2: `Info.cshtml`** renders `_Steps` partial (step 1 active), a form bound to `WizardInfoVm` (Name required, Type `<select>` over `PlanType`, Objective/Description/Scope textareas), `@Html.AntiForgeryToken()`, one `ep-primary` "Wizard.Next" button.

- [ ] **Step 3: Write `WizardStep1Tests.cs`.**
```csharp
[Fact] public async Task Post_info_creates_draft_and_redirects_to_teams() {
  /* login manager; PostFormAsync Name=P1,Type=Emergency → 302 /admin/plans/create/{id}/teams;
     scope: Plan exists Status=Draft CreatedByUserId=manager; a PlanActivator row for manager exists */ }
[Fact] public async Task Missing_name_re_renders_step1() { /* no Name → 200 body still step 1 */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/WizardStep1Tests.cs
git commit -m "feat(web): create-plan wizard step 1 (plan info -> draft)"
```

---

## Task 10: Create-Plan Wizard — Step 2 (Teams & members)

**Files:**
- Modify: `PlanWizardController.cs` (Teams GET/POST)
- Create: `Areas/Admin/Models/WizardTeamsVm.cs`
- Create: `Areas/Admin/Views/PlanWizard/Teams.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/WizardStep2Tests.cs`

**Interfaces:**
- Consumes: the Draft `Plan` (by id), `IRepository<User>` (read-only user list), `IRepository<Team>`, `IRepository<TeamMembership>`.
- Produces: adds `Team`(s) + `TeamMembership`(s) to the draft; redirects to `/admin/plans/create/{id}/tasks`. `WizardTeamsVm{ Guid PlanId; List<TeamInput> Teams; IReadOnlyList<UserOption> AllUsers }`, `TeamInput{ string Name; Guid? TeamLeaderUserId; List<Guid> MemberUserIds }`, `UserOption{ Guid Id; string Label }`.

- [ ] **Step 1: Ownership guard helper** on the controller (reused by steps 2–4): load the plan by id; if null → `AppException.NotFound`; if `Status != Draft` → redirect to `/admin/plans/{id}` (already finalized); if `CreatedByUserId != _me.UserId` and not SystemAdmin → `AppException.Forbidden`. Call it at the top of every wizard step action.

- [ ] **Step 2: Teams GET** shows existing teams for the draft + an add-team form (name, optional leader `<select>` from users, multi-select members). **Teams POST** adds one team + memberships via `Repository.AddAsync` per row (never mutate a tracked collection), one `SaveChangesAsync`; supports adding multiple teams across multiple posts (each post adds one team and re-renders, with a "Wizard.Next" that goes to tasks once at least one team exists). Enforce: at least one team with at least one member before advancing (server-side).

- [ ] **Step 3: `Teams.cshtml`** — `_Steps` (step 2), list current teams, add-team form, `ep-primary` Next (disabled/blocked server-side until ≥1 team).

- [ ] **Step 4: Write `WizardStep2Tests.cs`.**
```csharp
[Fact] public async Task Add_team_with_members_persists() { /* team + memberships exist for the draft */ }
[Fact] public async Task Advancing_without_team_is_blocked() { /* POST next w/ no team → re-render step 2 */ }
[Fact] public async Task Foreign_draft_forbidden() { /* manager2 opening manager1's draft → 302 denied */ }
```

- [ ] **Step 5: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/WizardStep2Tests.cs
git commit -m "feat(web): create-plan wizard step 2 (teams & members)"
```

---

## Task 11: Create-Plan Wizard — Step 3 (Tasks)

**Files:**
- Modify: `PlanWizardController.cs` (Tasks GET/POST)
- Create: `Areas/Admin/Models/WizardTasksVm.cs`
- Create: `Areas/Admin/Views/PlanWizard/Tasks.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/WizardStep3Tests.cs`

**Interfaces:**
- Consumes: draft `Plan`, its `Team`s, `IRepository<TaskTemplate>`.
- Produces: adds `TaskTemplate{TeamId, Title, Order, Duration}` rows; redirects to `/admin/plans/create/{id}/review`. `WizardTasksVm{ Guid PlanId; IReadOnlyList<TeamTasks> Teams }`, `TeamTasks{ Guid TeamId; string TeamName; List<TaskInput> Tasks }`, `TaskInput{ string Title; int Order; int DurationMinutes }`.

- [ ] **Step 1: Tasks GET/POST** (ownership guard first). GET lists each team with its current task templates + an add-task form (Title, Order, Duration in minutes → `TimeSpan.FromMinutes`). POST adds one `TaskTemplate` via `AddAsync`, one `SaveChangesAsync`. Advancing requires **at least one team to have at least one task** (server-side).

- [ ] **Step 2: `Tasks.cshtml`** — `_Steps` (step 3), per-team task lists + add-task forms, `ep-primary` Next.

- [ ] **Step 3: Write `WizardStep3Tests.cs`.**
```csharp
[Fact] public async Task Add_task_persists_with_duration() { /* TaskTemplate exists, Duration == 30 min */ }
[Fact] public async Task Advancing_without_any_task_blocked() { /* → re-render step 3 */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/WizardStep3Tests.cs
git commit -m "feat(web): create-plan wizard step 3 (tasks)"
```

---

## Task 12: Create-Plan Wizard — Step 4 (Shifts & review → Ready) + step guard

**Files:**
- Modify: `PlanWizardController.cs` (Review GET/POST) + a `RequireStep` navigation guard
- Create: `Areas/Admin/Models/WizardReviewVm.cs`
- Create: `Areas/Admin/Views/PlanWizard/Review.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/WizardStep4Tests.cs`

**Interfaces:**
- Consumes: draft `Plan` + teams/members/tasks, `IRepository<ShiftAssignment>`.
- Produces: adds `ShiftAssignment{TeamId, UserId, Shift, Date, SubstituteForUserId?}` rows; on Finish sets `Plan.Status = Ready`; redirects to `/admin/plans/{id}`. `WizardReviewVm{ Guid PlanId; List<RosterInput> Roster; ReviewReadback Readback }`, `RosterInput{ Guid TeamId; Guid UserId; ShiftBand Shift; DateTime Date; Guid? SubstituteForUserId }`.

- [ ] **Step 1: `RequireStep` guard** — a private method that, given the draft, verifies the prerequisites of the requested step are met (step 2 needs ≥1 team+member; step 3 needs ≥1 task; step 4 needs the roster form) and otherwise redirects back to the earliest unmet step. Call it in every step GET so a user cannot deep-link past unmet steps (spec §9 "wizard-navigation guard").

- [ ] **Step 2: Review GET** shows the roster editor (per member: Shift `<select>`, Date, optional Substitute `<select>`) + a **read-back** of the whole plan. **Review POST (Finish)** stages `ShiftAssignment` rows, validates the roster is non-empty and substitute ids reference valid users on the plan, sets `Plan.Status=Ready` on the tracked plan, one `SaveChangesAsync`, redirect to `/admin/plans/{id}`.

- [ ] **Step 3: `Review.cshtml`** — `_Steps` (step 4), roster editor, read-back, `ep-primary` "Wizard.Finish".

- [ ] **Step 4: Write `WizardStep4Tests.cs`.**
```csharp
[Fact] public async Task Finish_sets_plan_ready_and_persists_roster() { /* ShiftAssignment rows exist; Plan.Status==Ready; redirect /admin/plans/{id} */ }
[Fact] public async Task Empty_roster_blocks_finish() { /* re-render step 4 */ }
[Fact] public async Task Deeplink_to_review_before_prereqs_redirects_back() { /* fresh draft GET review → redirect to teams/tasks */ }
```

- [ ] **Step 5: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/WizardStep4Tests.cs
git commit -m "feat(web): create-plan wizard step 4 (shifts & review -> ready) + step guard"
```

---

## Task 13: Activate (Plan Detail button + confirmation + POST)

**Files:**
- Modify: `PlansController.cs` (Activate action)
- Modify: `Areas/Admin/Views/Plans/Detail.cshtml` (oversized launch button + confirm)
- Test: `tests/ExecPlan.IntegrationTests/Web/ActivateTests.cs`

**Interfaces:**
- Consumes: `IActivationService.ActivateAsync(planId, actingUserId, ct)` → `Guid activationId`; `ICurrentUser`.
- Produces: `POST /admin/plans/{id}/activate` → redirect to `/admin/activations/{activationId}`; maps `AppException` (Forbidden→denied, Conflict/Validation→back to Detail with message).

- [ ] **Step 1: `PlansController.Activate`.**
```csharp
[HttpPost("{id:guid}/activate")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
{
    var activationId = await _activation.ActivateAsync(id, _me.UserId!.Value, ct); // AppException flows to middleware
    return Redirect($"/admin/activations/{activationId}");
}
```
Inject `IActivationService` into `PlansController`.

- [ ] **Step 2: Detail view** — replace the Task-8 placeholder region: when `Model.Status == Ready`, render the oversized amber launch button inside a confirm dialog (Bootstrap modal or a confirm form) posting to `/admin/plans/{id}/activate` with `@Html.AntiForgeryToken()`; label `Plans.Activate` (Arabic «إطلاق الخطة»), confirmation text `Plans.ActivateConfirm`. When `Draft`, show a disabled hint instead. Use class `btn-launch` (the only amber button).

- [ ] **Step 3: Write `ActivateTests.cs`.** Arrange a Ready plan (reuse the wizard or seed via services):
```csharp
[Fact] public async Task Activate_ready_plan_redirects_to_dashboard() { /* 302 /admin/activations/{guid} */ }
[Fact] public async Task Activate_unauthorized_activator_denied() {
  /* a manager who is neither creator nor an authorized activator → service throws Forbidden → 302 /admin/denied */ }
[Fact] public async Task Activate_draft_plan_shows_conflict() { /* Draft → service Conflict → back to detail with message */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/ActivateTests.cs
git commit -m "feat(web): activate plan (oversized launch + confirm)"
```

---

## Task 14: Live Dashboard — server render + snapshot JSON + Leader scoping + leader landing

**Files:**
- Create: `src/ExecPlan.Api/Areas/Admin/Controllers/ActivationsController.cs` (Dashboard, Snapshot, plus the leader `Index` landing)
- Create: `Areas/Admin/Models/DashboardVm.cs`
- Create: `Areas/Admin/Views/Activations/Dashboard.cshtml`, `Index.cshtml` (leader landing list)
- Test: `tests/ExecPlan.IntegrationTests/Web/DashboardTests.cs`

**Interfaces:**
- Consumes: `IDashboardService.GetSnapshotAsync(activationId, ct)` → `DashboardDto`; `IRepository<PlanActivation>`/`IRepository<Team>` for the leader landing.
- Produces: `GET /admin/activations/{id}` (HTML), `GET /admin/activations/{id}/snapshot` (JSON `DashboardDto`), `GET /admin/activations` (leader landing). `DashboardVm` wraps `DashboardDto` + `bool CanAct` (`SystemAdmin`/`PlanManager`).

- [ ] **Step 1: `ActivationsController`.** Class gate `[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Roles = "SystemAdmin,PlanManager,TeamLeader")]`. Inject `IDashboardService _dash`, `IUnitOfWork _uow`, `ICurrentUser _me` (Task 15 adds the action services).

**Leader scoping is enforced in the controller** — `IDashboardService.GetSnapshotAsync(activationId, ct)` is deliberately actor-agnostic (it has no user parameter; Manager/Admin and the close-summary reuse it as-is). The Phase 1 API controller does the object-level "own teams" check itself (`src/ExecPlan.Api/Controllers/ActivationsController.cs:73-83`); the MVC panel replicates it via one private guard used by **both** the HTML and JSON endpoints (a Leader must not bypass it by hitting `/snapshot` directly):
```csharp
// PRD §14 "own teams" / DEC-17 — a TeamLeader may view an activation only if they lead
// at least one participating team. Manager/Admin: no restriction. Mirrors the proven API path.
private async Task EnsureMayViewAsync(Guid activationId, CancellationToken ct)
{
    if (_me.Role != UserRole.TeamLeader) return;
    var participants = await _uow.Repo<ActivationParticipant>()
        .ListAsync(p => p.ActivationId == activationId, ct);
    var teamIds = participants.Select(p => p.TeamId).Distinct().ToList();
    var teams = await _uow.Repo<Team>().ListAsync(t => teamIds.Contains(t.Id), ct);
    var leadsAny = _me.UserId is not null && teams.Any(t => t.TeamLeaderUserId == _me.UserId);
    if (!leadsAny) throw AppException.Forbidden("You do not lead a team participating in this activation.");
}

[HttpGet("{id:guid}")]
public async Task<IActionResult> Dashboard(Guid id, CancellationToken ct)
{
    await EnsureMayViewAsync(id, ct);                     // Forbidden → middleware → /admin/denied
    var dto = await _dash.GetSnapshotAsync(id, ct);
    if (dto.Status == ActivationStatus.Closed) return Redirect($"/admin/activations/{id}/summary");
    return View(new DashboardVm(dto,
        User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager))));
}

[HttpGet("{id:guid}/snapshot")]
public async Task<IActionResult> Snapshot(Guid id, CancellationToken ct)
{
    await EnsureMayViewAsync(id, ct);                     // same guard — dashboard.js polls this
    return Json(await _dash.GetSnapshotAsync(id, ct));
}
```
Leader `Index` (`GET /admin/activations`, action `[HttpGet("")]`): list the **active** `PlanActivation`s whose plan has a team led by `_me.UserId`, each linking to its dashboard. Compute it from `ActivationParticipant`→`Team.TeamLeaderUserId == _me.UserId` on `Status==Active` activations (same shape as the guard). Manager/Admin hitting `/admin/activations` redirect to `/admin/plans`. This is the thin leader landing referenced by `HomeController.Index` (spec §7.9).

- [ ] **Step 2: `Dashboard.cshtml`.** `_Layout`; render five `counter-tile`s (`Dash.Total/Pending/Ready/Escalated/Inducted`, `ready`/`escalated` colour classes), `ResponseRate`/`TaskCompletionRate` as percentages, the team ranking table (already sorted by `Score` desc — render in order; mark first row "best", last "delayed"), the overdue list (red), and the latest-50 event feed. If `Model.CanAct` and `Status==Active`, render the action bar: Run-escalation (form POST), Broadcast (modal→POST), Close (confirm→POST) — added/enabled in Task 15. Add `data-activation-id="@Model.Dto.ActivationId"` on the root element and a `@section scripts { <script src="/lib/signalr/signalr.min.js"></script><script src="/js/dashboard.js"></script> }` (dashboard.js lands in Task 16; the tag can reference it now).

- [ ] **Step 3: Write `DashboardTests.cs`.** Arrange an active activation (activate a Ready plan via `IActivationService` in a scope):
```csharp
[Fact] public async Task Dashboard_renders_five_counters_and_rates() { /* body contains the 5 counter labels + numbers */ }
[Fact] public async Task Team_ranking_sorted_best_first() { /* rows in Score-desc order */ }
[Fact] public async Task Snapshot_returns_json_dto() { /* GET .../snapshot → application/json, has TotalParticipants */ }
[Fact] public async Task Leader_cannot_open_foreign_activation() { /* leader of another team → 302 /admin/denied (Forbidden) */ }
[Fact] public async Task Member_cannot_reach_dashboard() { /* member blocked at login already; direct GET → 302 login */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/DashboardTests.cs
git commit -m "feat(web): live dashboard server render + snapshot json + leader scoping"
```

---

## Task 15: Dashboard actions (escalation / broadcast / close) + Activation Summary

**Files:**
- Modify: `ActivationsController.cs` (RunEscalation, Broadcast, Close, Summary)
- Modify: `Areas/Admin/Views/Activations/Dashboard.cshtml` (wire the action bar)
- Create: `Areas/Admin/Views/Activations/Summary.cshtml`
- Test: `tests/ExecPlan.IntegrationTests/Web/DashboardActionsTests.cs`

**Interfaces:**
- Consumes: `IEscalationService.RunCycleAsync`, `BroadcastService.BroadcastAsync`, `ExecutionService.CloseAsync`, `IDashboardService.GetSnapshotAsync`.
- Produces: `POST run-escalation` (ManagerOrAdmin) → back to dashboard with a toast of `EscalationCycleResult`; `POST broadcast` → back to dashboard; `POST close` → redirect `/admin/activations/{id}/summary`; `GET summary` shows final counts (no actions).

- [ ] **Step 1: Actions** — each `[Authorize(..., Policy = ManagerOrAdmin)]` + `[ValidateAntiForgeryToken]`.
```csharp
[HttpPost("{id:guid}/run-escalation")][Authorize(AuthenticationSchemes=AuthPolicies.AdminCookieScheme,Policy=AuthPolicies.ManagerOrAdmin)][ValidateAntiForgeryToken]
public async Task<IActionResult> RunEscalation(Guid id, CancellationToken ct)
{ var r = await _esc.RunCycleAsync(id, ct); TempData["toast"]=$"+{r.AttemptsAdded}/{r.Inducted}"; return Redirect($"/admin/activations/{id}"); }

[HttpPost("{id:guid}/broadcast")][...ManagerOrAdmin][ValidateAntiForgeryToken]
public async Task<IActionResult> Broadcast(Guid id, string body, CancellationToken ct)
{ await _broadcast.BroadcastAsync(id, body, ct); return Redirect($"/admin/activations/{id}"); }

[HttpPost("{id:guid}/close")][...ManagerOrAdmin][ValidateAntiForgeryToken]
public async Task<IActionResult> Close(Guid id, CancellationToken ct)
{ await _exec.CloseAsync(id, ct); return Redirect($"/admin/activations/{id}/summary"); }

[HttpGet("{id:guid}/summary")][Authorize(AuthenticationSchemes=AuthPolicies.AdminCookieScheme,Policy=AuthPolicies.ManagerOrAdmin)]
public async Task<IActionResult> Summary(Guid id, CancellationToken ct)
{ var dto = await _dash.GetSnapshotAsync(id, ct);
  if (dto.Status != ActivationStatus.Closed) return Redirect($"/admin/activations/{id}");
  return View(new DashboardVm(dto, false)); }
```
Inject `IEscalationService`, `BroadcastService`, `ExecutionService` into the controller.

- [ ] **Step 2: Wire the action bar** in `Dashboard.cshtml` (forms posting to the three endpoints, Broadcast opens a Bootstrap modal with a `body` textarea, Close behind a confirm using `Dash.CloseConfirm`; render `TempData["toast"]` if present). `Summary.cshtml` renders the five final counters + rates + final ranking, **no action bar, no scripts** (§16 "no live updates").

- [ ] **Step 3: Write `DashboardActionsTests.cs`.**
```csharp
[Fact] public async Task Run_escalation_returns_to_dashboard() { }
[Fact] public async Task Broadcast_persists_message() { /* BroadcastMessage row exists */ }
[Fact] public async Task Close_redirects_to_summary_and_marks_closed() { /* activation Status==Closed; summary 200 no action bar */ }
[Fact] public async Task Summary_of_active_redirects_to_dashboard() { }
[Fact] public async Task Leader_cannot_close() { /* leader POST close → 403 */ }
```

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api tests/ExecPlan.IntegrationTests/Web/DashboardActionsTests.cs
git commit -m "feat(web): dashboard actions (escalation/broadcast/close) + activation summary"
```

---

## Task 16: Realtime dashboard client + cookie-authenticated SignalR test

**Files:**
- Create: `src/ExecPlan.Api/wwwroot/js/dashboard.js`
- Test: `tests/ExecPlan.IntegrationTests/Web/DashboardRealtimeTests.cs`

**Interfaces:**
- Consumes: `/hubs/dashboard` (existing hub, `JoinActivation(Guid)`), `/admin/activations/{id}/snapshot` (Task 14).
- Produces: `dashboard.js` connects with the same-origin cookie, joins the activation group, re-fetches the snapshot on each push, re-renders counters/rates/ranking/overdue/feed, auto-reconnects, and falls back to ~5s polling when the hub is down; on a Closed snapshot it redirects to the summary.

- [ ] **Step 1: `wwwroot/js/dashboard.js`.**
```javascript
(function () {
  var root = document.querySelector('[data-activation-id]');
  if (!root) return;
  var id = root.getAttribute('data-activation-id');
  var snapshotUrl = '/admin/activations/' + id + '/snapshot';
  var summaryUrl = '/admin/activations/' + id + '/summary';
  var pollTimer = null;

  function render(dto) {
    if (!dto) return;
    if (dto.status === 1 /* Closed */) { window.location = summaryUrl; return; }
    setText('n-total', dto.totalParticipants); setText('n-pending', dto.pendingCount);
    setText('n-ready', dto.readyCount); setText('n-escalated', dto.escalatedCount);
    setText('n-inducted', dto.inductedCount);
    setText('n-response', Math.round(dto.responseRate * 100) + '%');
    setText('n-taskrate', Math.round(dto.taskCompletionRate * 100) + '%');
    // ranking/overdue/feed re-render by innerHTML from dto.teams/overdue/events (ids in Dashboard.cshtml)
  }
  function setText(id, v){ var el=document.getElementById(id); if(el) el.textContent=v; }
  function refresh(){ return fetch(snapshotUrl, {credentials:'same-origin'}).then(function(r){return r.json();}).then(render).catch(function(){}); }
  function startPoll(){ if(!pollTimer) pollTimer=setInterval(refresh, 5000); }
  function stopPoll(){ if(pollTimer){ clearInterval(pollTimer); pollTimer=null; } }

  var conn = new signalR.HubConnectionBuilder().withUrl('/hubs/dashboard').withAutomaticReconnect().build();
  conn.on('DashboardChanged', refresh);
  conn.on('ActivationClosed', function(){ window.location = summaryUrl; });
  conn.onreconnecting(startPoll); conn.onreconnected(function(){ stopPoll(); refresh(); });
  conn.onclose(startPoll);
  conn.start().then(function(){ return conn.invoke('JoinActivation', id); }).then(refresh).catch(startPoll);
})();
```
Match the JSON property casing to what `Json(dto)` emits (default camelCase) and the hub method/event names to the existing `DashboardHub`/`SignalRRealtimeNotifier` (`JoinActivation`, `DashboardChanged`, `ActivationClosed`). Confirm the actual event names in `src/ExecPlan.Api/Hubs/` during implementation and align.

- [ ] **Step 2: Ensure `Dashboard.cshtml` counter elements carry the ids** `n-total/n-pending/n-ready/n-escalated/n-inducted/n-response/n-taskrate` and container ids for ranking/overdue/feed used by `render`.

- [ ] **Step 3: Write `DashboardRealtimeTests.cs`** — a **cookie-authenticated** `HubConnection` (mirror the Phase 1 JWT hub test but authenticate via the cookie): build the connection against `_factory.Server`, attach the auth cookie obtained from `LoginAsync`, `JoinActivation(id)`, then trigger an acknowledge (call `AcknowledgeService` in a scope or POST an ack) and assert a `DashboardChanged` push arrives within a timeout.
```csharp
[Fact] public async Task Cookie_client_receives_dashboard_changed_on_acknowledge() { /* see above */ }
```
Use `_factory.Server.CreateHandler()` and set the `Cookie` header / `HttpMessageHandlerFactory` on the `HubConnectionBuilder` so the cookie rides the negotiate + WS handshake. If wiring the cookie into the test hub client proves environment-specific, assert the same push over the existing JWT test path AND assert `GET /admin/activations/{id}/snapshot` reflects the change (documented fallback), and note it in PROGRESS.

- [ ] **Step 4: Run → PASS. Commit.**
```bash
git add src/ExecPlan.Api/wwwroot/js/dashboard.js tests/ExecPlan.IntegrationTests/Web/DashboardRealtimeTests.cs
git commit -m "feat(web): realtime dashboard client + cookie signalr test"
```

---

## Task 17: §21 web acceptance (end-to-end) + living docs + final green

**Files:**
- Test: `tests/ExecPlan.IntegrationTests/Web/WebAcceptanceTests.cs`
- Modify: `docs/DECISIONS.md` (add DEC-20…26), `docs/PROGRESS.md` (Phase 2 entry)
- Modify: `CLAUDE.md` (note the web admin is now built; run/paths)

**Interfaces:**
- Consumes: everything above.
- Produces: the single end-to-end acceptance test and the updated living docs.

- [ ] **Step 1: Write `WebAcceptanceTests.cs`** — one test, all in-process under the default `ar` culture:
```csharp
[Fact]
public async Task Manager_creates_activates_and_watches_dashboard_in_arabic()
{
    var client = _factory.CreateClient(new(){ HandleCookies=true, AllowAutoRedirect=false });
    await WebTestHelpers.LoginAsync(client, "manager", "Passw0rd!");
    // wizard step 1..4 via PostFormAsync → Ready plan
    // GET /admin/plans/{id} → contains dir="rtl" and the Arabic launch label
    // POST activate → 302 /admin/activations/{activationId}
    // GET dashboard → 200, contains the five Arabic counter labels and numbers
    // assert no request in the flow went to /api/v1 (all in-process)
}
```
Assert the rendered dashboard is Arabic (`dir="rtl"`, `Dash.Ready` Arabic label present).

- [ ] **Step 2: Update `docs/DECISIONS.md`** — append DEC-20…26 exactly as enumerated in the design spec §13 (area/scheme, in-process calls, custom theme resolving DEC-12, server-incremental wizard, middleware content-negotiation, Organizations screen for FR-ADM-1, dashboard snapshot-refresh + poll fallback).

- [ ] **Step 3: Update `docs/PROGRESS.md`** — dated Phase 2 entry: what shipped, the head commit, test count, and any deferred note from Task 16.

- [ ] **Step 4: Update `CLAUDE.md`** — under "What this is"/"Build/test/run", note the admin panel is implemented (routes under `/admin`, cookie login, seeded demo accounts) and that assets are self-hosted.

- [ ] **Step 5: Full suite green.**
Run: `dotnet build` (0 warnings) then `dotnet test`.
Expected: all Phase 1 (102) + all new Phase 2 tests pass.

- [ ] **Step 6: Commit.**
```bash
git add tests/ExecPlan.IntegrationTests/Web/WebAcceptanceTests.cs docs/DECISIONS.md docs/PROGRESS.md CLAUDE.md
git commit -m "test(web): §21 acceptance e2e; docs: DEC-20..26, progress, claude.md"
```

---

## Global test & build commands

- Build: `dotnet build ExecPlan.sln` (expect 0 warnings)
- Full suite: `dotnet test` (expect all green — Phase 1 102 + Phase 2)
- Filter one class: `dotnet test --filter FullyQualifiedName~<ClassName>`
- Run the app (manual smoke, Development = ar default, seed on): `dotnet run --project src/ExecPlan.Api` then browse `http://localhost:5080/admin/login` and sign in `manager` / `Passw0rd!`.

## Notes for the implementer

- Reuse the **existing** `TestAppFactory` (SQLite in-memory + seed + `TestClock`); never spin up a second factory or touch the live SQL Server from tests.
- Never call `/api/v1` from an admin controller — inject and call the Application service.
- Every admin controller carries `[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, …)]`; every POST carries `[ValidateAntiForgeryToken]` and every form includes `@Html.AntiForgeryToken()`.
- Do not touch `docs/*.pdf`; do not run `git clean`.
- Keep the `ExecPlan.Application`/`ExecPlan.Domain` projects free of EF Core and SignalR references.
```
