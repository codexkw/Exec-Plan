# EXECPLAN Backend Spine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the EXECPLAN backend spine — a .NET 9 clean-architecture solution implementing the full activation cycle (auth → plan authoring data → activation snapshot → readiness → escalation → live dashboard) over EF Core/SQL Server, exposed as a JWT REST API with an in-process SignalR hub and a CLI escalation entrypoint, verified by xUnit tests against the PRD §21 backend acceptance criteria.

**Architecture:** Clean architecture with a strict dependency direction `Domain ← Application ← Infrastructure ← {Api, Cli}`. The `Application` project holds all domain logic behind abstractions (`IUnitOfWork`, repositories, `INotificationProvider`, `IRealtimeNotifier`, `IClock`) and references neither EF Core nor SignalR, so the API, the CLI, and any future scheduler trigger identical behavior. Activation/escalation/broadcast each run in one transaction; the notification provider stages rows and the calling service performs the single save. A running activation is an immutable snapshot.

**Tech Stack:** .NET 9, ASP.NET Core (MVC + Web API + SignalR), Entity Framework Core 9 (SQL Server + SQLite provider switch), `Microsoft.AspNetCore.Identity.PasswordHasher`, JWT bearer auth + cookie auth, xUnit + FluentAssertions + `Microsoft.AspNetCore.Mvc.Testing`.

## Global Constraints

- **Target framework:** `net9.0` for every project.
- **Pin all `Microsoft.*` / EF Core / `Microsoft.AspNetCore.Authentication.JwtBearer` packages to `9.0.*`** — never let restore pull a 10.x preview. (Portfolio gotcha.)
- **Secrets never committed.** Real connection string / JWT signing key live only in git-ignored `appsettings.Development.json`, user-secrets, or env vars. Committed files use placeholders. The repo `.gitignore` already excludes `appsettings.Development.json`.
- **Guid PKs assigned in the entity constructor** (`Id = Guid.NewGuid()`). Add child entities via the repository (`AddAsync`), never by mutating a tracked parent's collection navigation.
- **One transaction** for activation, escalation, broadcast (NFR-8): provider stages rows, service saves once.
- **Snapshot immutability:** runtime rows freeze team name as text and user/team ids; they never read mutable template structure for frozen values.
- **Only `ResponseStatus` counts as a response.** Opening/viewing/completing tasks never marks "responded".
- **`AsSplitQuery()`** on any query with 2+ collection `Include`s.
- **Time:** store UTC everywhere; resolve shifts against Asia/Kuwait (`TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time")`, fallback IANA `"Asia/Kuwait"`). Shifts: Morning 06:00–14:00, Evening 14:00–22:00, Night 22:00–06:00; a night shift after midnight resolves to the **previous** Kuwait calendar day's roster.
- **Escalation threshold** is config `Escalation:DefaultThreshold` (default 5), copied onto each activation at creation.
- **Two-layer authorization:** per-endpoint role gate **and** per-record queryset filtering by the acting user's scope.
- Reference doc: `docs/superpowers/specs/2026-06-30-execplan-backend-spine-design.md` (authoritative design); `PRD.md` (authoritative product rules).

---

## File Structure Map

```
backend/
  ExecPlan.sln
  Directory.Build.props                      # net9.0, nullable enable, LangVersion, version pinning notes
  src/
    ExecPlan.Domain/
      Common/BaseEntity.cs
      Enums/*.cs                             # UserRole, PlanType, PlanStatus, ShiftBand, ActivationStatus, ParticipantStatus, ExecTaskStatus, NotificationKind, ContactKind
      Entities/*.cs                          # 15 domain entities + owned PlanContact/PlanActivator
    ExecPlan.Application/
      Abstractions/IClock.cs, IUnitOfWork.cs, IRepository.cs, INotificationProvider.cs, IRealtimeNotifier.cs, ICurrentUser.cs
      Common/AppException.cs (NotFound/Forbidden/Conflict/Validation)
      Shifts/KuwaitShiftCalculator.cs, ShiftResolution.cs
      Auth/IAuthService.cs, AuthService.cs, TokenModels.cs, IJwtTokenFactory.cs, IPasswordHasher.cs
      Plans/ (CRUD service interfaces + DTOs)
      Activation/ActivationService.cs, IActivationService.cs
      Execution/AcknowledgeService.cs, ExecutionService.cs
      Escalation/EscalationService.cs, IEscalationService.cs
      Dashboard/DashboardService.cs, DashboardDto.cs
      Broadcast/BroadcastService.cs
      DependencyInjection.cs                 # AddApplication()
    ExecPlan.Infrastructure/
      Persistence/ExecPlanDbContext.cs
      Persistence/Configurations/*.cs        # IEntityTypeConfiguration per entity
      Persistence/Repository.cs, UnitOfWork.cs
      Persistence/RefreshToken.cs            # infra-only entity
      Persistence/Migrations/*               # EF migrations (SqlServer)
      Auth/JwtTokenFactory.cs, IdentityPasswordHasher.cs, RefreshTokenStore.cs
      Notifications/DatabasePlaceholderProvider.cs
      Time/KuwaitClock.cs
      Seed/DataSeeder.cs
      DependencyInjection.cs                 # AddInfrastructure(config)
    ExecPlan.Api/
      Program.cs
      Auth/CurrentUser.cs, AuthPolicies.cs
      Controllers/*.cs                       # Auth + CRUD + Activation + Execution
      Hubs/DashboardHub.cs, SignalRRealtimeNotifier.cs
      Localization/ (RequestLocalization wiring, SetLanguage endpoint)
      Areas/Admin/                           # MVC area shell only in Phase 1
      appsettings.json                       # placeholders, no secrets
      appsettings.Development.json           # GIT-IGNORED — real conn string + signing key
    ExecPlan.Cli/
      Program.cs                             # run-escalation command
  tests/
    ExecPlan.UnitTests/                      # shift, escalation threshold, ranking, guards
    ExecPlan.IntegrationTests/
      TestAppFactory.cs                      # WebApplicationFactory over SQLite in-memory
      SqliteFixture.cs
      *Tests.cs
```

---

## Locked Contracts

These names/types are referenced by every task. Do not rename without updating all tasks.

### Enums (`ExecPlan.Domain.Enums`)
```csharp
public enum UserRole { SystemAdmin = 0, PlanManager = 1, TeamLeader = 2, TeamMember = 3 }
public enum PlanType { Daily, Weekly, Emergency, Guard, Transport, Maintenance, It, Inspection, General }
public enum PlanStatus { Draft = 0, Ready = 1 }
public enum ShiftBand { Morning = 0, Evening = 1, Night = 2 }
public enum ActivationStatus { Active = 0, Closed = 1 }
public enum ParticipantStatus { Pending = 0, Ready = 1, Escalated = 2, Inducted = 3 }
public enum ExecTaskStatus { Pending = 0, Done = 1 }
public enum NotificationKind { Notification = 0, Broadcast = 1 }
public enum ContactKind { Contact = 0, Emergency = 1 }
```

### Application abstractions
```csharp
public interface IClock { DateTime UtcNow { get; } }

public interface IRepository<T> where T : BaseEntity {
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    IQueryable<T> Query();                       // no-tracking queryable
    IQueryable<T> Tracking();                     // tracked queryable
    Task AddAsync(T entity, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken ct = default);
    void Remove(T entity);
}

public interface IUnitOfWork {
    IRepository<T> Repo<T>() where T : BaseEntity;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct = default); // no-op-able on Sqlite
}

public interface INotificationProvider {
    // Stages rows into the UoW; does NOT save. Returns the created log/attempt entities.
    NotificationLog StageNotification(Guid activationId, Guid recipientUserId, NotificationKind kind, string body, DateTime utcNow);
    CallAttempt StageCallAttempt(Guid activationId, Guid participantId, int attemptNumber, DateTime utcNow);
}

public interface IRealtimeNotifier {
    Task DashboardChangedAsync(Guid activationId, CancellationToken ct = default);
    Task ActivationClosedAsync(Guid activationId, CancellationToken ct = default);
}

public interface ICurrentUser { Guid? UserId { get; } UserRole? Role { get; } bool IsInRole(UserRole r); }
```

### Service interfaces (signatures later tasks consume)
```csharp
public interface IAuthService {
    Task<TokenPair> LoginAsync(string userName, string password, CancellationToken ct = default);
    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<AppUserPrincipal> ValidateCredentialsAsync(string userName, string password, CancellationToken ct = default); // cookie sign-in
}
public record TokenPair(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);
public record AppUserPrincipal(Guid UserId, UserRole Role, string FullName, string UserName);

public interface IActivationService {
    Task<Guid> ActivateAsync(Guid planId, Guid actingUserId, CancellationToken ct = default);
}
public interface IEscalationService {
    Task<EscalationCycleResult> RunCycleAsync(Guid activationId, CancellationToken ct = default);
}
public record EscalationCycleResult(int AttemptsAdded, int Inducted);

public interface IDashboardService {
    Task<DashboardDto> GetSnapshotAsync(Guid activationId, CancellationToken ct = default);
}
```

### DashboardDto
```csharp
public record DashboardDto(
    Guid ActivationId, ActivationStatus Status, ShiftBand Shift, DateTime RosterDate,
    int Total, int Pending, int Ready, int Escalated, int Inducted,
    double ResponseRate, double TaskCompletionRate,
    IReadOnlyList<TeamRow> Teams, IReadOnlyList<OverdueTask> Overdue, IReadOnlyList<FeedEvent> Events);
public record TeamRow(Guid TeamId, string TeamName, int Members, int ReadyCount, int TasksTotal, int TasksDone, double Score);
public record OverdueTask(Guid TaskId, string Title, Guid ParticipantUserId, DateTime DueAtUtc);
public record FeedEvent(DateTime AtUtc, string Type, string Text);
```

---

## Wave 0 — Solution scaffold

### Task 1: Create the solution, projects, references, and a green smoke build

**Files:**
- Create: `backend/ExecPlan.sln`, `backend/Directory.Build.props`
- Create: the 5 `src/*` `.csproj` + 2 `tests/*` `.csproj`
- Create: `src/ExecPlan.Api/appsettings.json` (placeholders), `src/ExecPlan.Api/appsettings.Development.json` (git-ignored)
- Test: `tests/ExecPlan.UnitTests/SmokeTest.cs`

**Interfaces:**
- Produces: the project graph and build. No app types yet.

- [ ] **Step 1: Create the solution and projects**

```bash
cd backend
dotnet new sln -n ExecPlan
dotnet new classlib -n ExecPlan.Domain        -o src/ExecPlan.Domain        -f net9.0
dotnet new classlib -n ExecPlan.Application    -o src/ExecPlan.Application    -f net9.0
dotnet new classlib -n ExecPlan.Infrastructure -o src/ExecPlan.Infrastructure -f net9.0
dotnet new web      -n ExecPlan.Api            -o src/ExecPlan.Api            -f net9.0
dotnet new console  -n ExecPlan.Cli            -o src/ExecPlan.Cli            -f net9.0
dotnet new xunit    -n ExecPlan.UnitTests      -o tests/ExecPlan.UnitTests    -f net9.0
dotnet new xunit    -n ExecPlan.IntegrationTests -o tests/ExecPlan.IntegrationTests -f net9.0
# remove template Class1.cs files
rm -f src/ExecPlan.Domain/Class1.cs src/ExecPlan.Application/Class1.cs src/ExecPlan.Infrastructure/Class1.cs
dotnet sln add src/ExecPlan.Domain src/ExecPlan.Application src/ExecPlan.Infrastructure src/ExecPlan.Api src/ExecPlan.Cli tests/ExecPlan.UnitTests tests/ExecPlan.IntegrationTests
```

- [ ] **Step 2: Wire project references (enforces the dependency rule)**

```bash
dotnet add src/ExecPlan.Application reference src/ExecPlan.Domain
dotnet add src/ExecPlan.Infrastructure reference src/ExecPlan.Application
dotnet add src/ExecPlan.Api reference src/ExecPlan.Infrastructure src/ExecPlan.Application
dotnet add src/ExecPlan.Cli reference src/ExecPlan.Infrastructure src/ExecPlan.Application
dotnet add tests/ExecPlan.UnitTests reference src/ExecPlan.Application src/ExecPlan.Domain
dotnet add tests/ExecPlan.IntegrationTests reference src/ExecPlan.Api src/ExecPlan.Infrastructure src/ExecPlan.Application src/ExecPlan.Domain
```

- [ ] **Step 3: Add `Directory.Build.props`** (shared settings + nullable)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Add NuGet packages (pinned 9.0.\*)**

```bash
dotnet add src/ExecPlan.Infrastructure package Microsoft.EntityFrameworkCore.SqlServer -v 9.0.*
dotnet add src/ExecPlan.Infrastructure package Microsoft.EntityFrameworkCore.Sqlite -v 9.0.*
dotnet add src/ExecPlan.Infrastructure package Microsoft.EntityFrameworkCore.Design -v 9.0.*
dotnet add src/ExecPlan.Infrastructure package Microsoft.Extensions.Identity.Core -v 9.0.*
dotnet add src/ExecPlan.Infrastructure package Microsoft.AspNetCore.Authentication.JwtBearer -v 9.0.*
dotnet add src/ExecPlan.Api package Microsoft.AspNetCore.Authentication.JwtBearer -v 9.0.*
dotnet add src/ExecPlan.Api package Microsoft.EntityFrameworkCore.Design -v 9.0.*
dotnet add tests/ExecPlan.IntegrationTests package Microsoft.AspNetCore.Mvc.Testing -v 9.0.*
dotnet add tests/ExecPlan.IntegrationTests package Microsoft.EntityFrameworkCore.Sqlite -v 9.0.*
dotnet add tests/ExecPlan.UnitTests package FluentAssertions -v 6.*
dotnet add tests/ExecPlan.IntegrationTests package FluentAssertions -v 6.*
```

- [ ] **Step 5: Create `appsettings.json` (placeholders, NO secrets)**

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Database": { "Provider": "SqlServer" },
  "ConnectionStrings": { "Default": "Server=83.229.86.221;Database=Exec-Plan;User Id=sa;Password=__SET_IN_DEV_OR_ENV__;TrustServerCertificate=True;Encrypt=True" },
  "Jwt": { "Issuer": "execplan", "Audience": "execplan", "SigningKey": "__SET_IN_DEV_OR_ENV__", "AccessTokenMinutes": 30, "RefreshTokenDays": 14 },
  "Escalation": { "DefaultThreshold": 5 },
  "Localization": { "DefaultCulture": "ar", "SupportedCultures": [ "ar", "en" ] }
}
```

- [ ] **Step 6: Create `appsettings.Development.json` (git-ignored — real dev secrets)**

> This file is excluded by `.gitignore`. Put the real `sa` password and a dev signing key here. Verify `git status` does NOT list it.

```json
{
  "ConnectionStrings": { "Default": "Server=83.229.86.221;Database=Exec-Plan;User Id=sa;Password=<REAL_DEV_PASSWORD>;TrustServerCertificate=True;Encrypt=True" },
  "Jwt": { "SigningKey": "<DEV-ONLY-32+CHAR-RANDOM-STRING>" }
}
```

- [ ] **Step 7: Write the smoke test**

```csharp
// tests/ExecPlan.UnitTests/SmokeTest.cs
public class SmokeTest { [Fact] public void Solution_builds_and_xunit_runs() => Assert.True(true); }
```

- [ ] **Step 8: Build + test**

Run: `dotnet build ExecPlan.sln` then `dotnet test`
Expected: build succeeds; 1 test passes.

- [ ] **Step 9: Verify no secret staged, then commit**

```bash
git status --porcelain | grep -i "appsettings.Development.json" && echo "ABORT: secret staged" || true
git add -A
git commit -m "chore: scaffold ExecPlan .NET 9 solution (clean architecture, pinned 9.0.*)"
```

---

## Wave 1 — Domain

### Task 2: BaseEntity + enums

**Files:**
- Create: `src/ExecPlan.Domain/Common/BaseEntity.cs`, `src/ExecPlan.Domain/Enums/Enums.cs`
- Test: `tests/ExecPlan.UnitTests/Domain/BaseEntityTests.cs`

**Interfaces:**
- Produces: `BaseEntity` (Guid Id ctor-assigned), all enums from Locked Contracts.

- [ ] **Step 1: Failing test**

```csharp
public class BaseEntityTests {
  private sealed class Sample : BaseEntity { }
  [Fact] public void New_entity_gets_nonempty_guid_id() => new Sample().Id.Should().NotBe(Guid.Empty);
  [Fact] public void Two_entities_get_distinct_ids() { var a=new Sample(); var b=new Sample(); a.Id.Should().NotBe(b.Id); }
}
```

- [ ] **Step 2: Run — expect compile failure (BaseEntity missing).**

- [ ] **Step 3: Implement**

```csharp
// Common/BaseEntity.cs
namespace ExecPlan.Domain.Common;
public abstract class BaseEntity {
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = default; // set by service/clock at insert; default avoids Date.Now in ctor
    public DateTime? UpdatedAtUtc { get; set; }
}
```
Add `Enums/Enums.cs` with the nine enums verbatim from Locked Contracts.

- [ ] **Step 4: Run tests — expect PASS.**
- [ ] **Step 5: Commit** `feat(domain): BaseEntity + enums`

### Task 3: Template-side entities

**Files:**
- Create under `src/ExecPlan.Domain/Entities/`: `Organization.cs`, `Department.cs`, `User.cs`, `Plan.cs`, `PlanContact.cs`, `PlanActivator.cs`, `Team.cs`, `TeamMembership.cs`, `ShiftAssignment.cs`, `TaskTemplate.cs`
- Test: `tests/ExecPlan.UnitTests/Domain/TemplateGraphTests.cs`

**Interfaces:**
- Produces: template entities. Field lists are authoritative — later EF configs and services depend on them.

- [ ] **Step 1: Failing test** (constructs the object graph in memory)

```csharp
public class TemplateGraphTests {
  [Fact] public void Can_build_plan_with_team_member_task_and_roster() {
    var org = new Organization { Name = "Municipality" };
    var dept = new Department { Name = "Ops", OrganizationId = org.Id };
    var user = new User { UserName="m1", FullName="Member One", Phone="+965", Role=UserRole.TeamMember, OrganizationId=org.Id, DepartmentId=dept.Id, PasswordHash="x" };
    var plan = new Plan { Name="Storm", Type=PlanType.Emergency, Objective="o", Description="d", Scope="s", Status=PlanStatus.Ready, CreatedByUserId=user.Id };
    var team = new Team { PlanId=plan.Id, Name="Alpha", TeamLeaderUserId=null };
    var tt = new TaskTemplate { TeamId=team.Id, Title="Inspect", Order=1, Duration=TimeSpan.FromMinutes(30) };
    var sa = new ShiftAssignment { TeamId=team.Id, UserId=user.Id, Shift=ShiftBand.Morning, Date=new DateTime(2026,6,30), SubstituteForUserId=null };
    plan.Id.Should().NotBe(Guid.Empty); tt.Duration.Should().Be(TimeSpan.FromMinutes(30)); sa.SubstituteForUserId.Should().BeNull();
  }
}
```

- [ ] **Step 2: Run — expect compile failure.**

- [ ] **Step 3: Implement entities** (complete code)

```csharp
// Organization.cs
public class Organization : BaseEntity { public string Name { get; set; } = ""; }
// Department.cs
public class Department : BaseEntity { public string Name { get; set; } = ""; public Guid OrganizationId { get; set; } }
// User.cs
public class User : BaseEntity {
  public string UserName { get; set; } = "";
  public string PasswordHash { get; set; } = "";
  public string FullName { get; set; } = "";
  public string Phone { get; set; } = "";
  public UserRole Role { get; set; }
  public Guid OrganizationId { get; set; }
  public Guid? DepartmentId { get; set; }
  public bool IsActive { get; set; } = true;
}
// Plan.cs
public class Plan : BaseEntity {
  public string Name { get; set; } = "";
  public PlanType Type { get; set; }
  public string Objective { get; set; } = "";
  public string Description { get; set; } = "";
  public string Scope { get; set; } = "";
  public PlanStatus Status { get; set; } = PlanStatus.Draft;
  public Guid CreatedByUserId { get; set; }
  public List<PlanContact> Contacts { get; set; } = new();
  public List<PlanActivator> Activators { get; set; } = new();
}
// PlanContact.cs
public class PlanContact : BaseEntity { public Guid PlanId { get; set; } public string Name { get; set; } = ""; public string Number { get; set; } = ""; public ContactKind Kind { get; set; } }
// PlanActivator.cs
public class PlanActivator : BaseEntity { public Guid PlanId { get; set; } public Guid UserId { get; set; } }
// Team.cs
public class Team : BaseEntity { public Guid PlanId { get; set; } public string Name { get; set; } = ""; public Guid? TeamLeaderUserId { get; set; } }
// TeamMembership.cs
public class TeamMembership : BaseEntity { public Guid TeamId { get; set; } public Guid UserId { get; set; } }
// ShiftAssignment.cs
public class ShiftAssignment : BaseEntity { public Guid TeamId { get; set; } public Guid UserId { get; set; } public ShiftBand Shift { get; set; } public DateTime Date { get; set; } public Guid? SubstituteForUserId { get; set; } }
// TaskTemplate.cs
public class TaskTemplate : BaseEntity { public Guid TeamId { get; set; } public string Title { get; set; } = ""; public int Order { get; set; } public TimeSpan Duration { get; set; } }
```

- [ ] **Step 4: Run tests — PASS.**
- [ ] **Step 5: Commit** `feat(domain): template-side entities`

### Task 4: Runtime-side entities

**Files:**
- Create under `Entities/`: `PlanActivation.cs`, `ActivationParticipant.cs`, `ExecutionTask.cs`, `NotificationLog.cs`, `CallAttempt.cs`, `ResponseStatus.cs`, `EscalationLog.cs`, `BroadcastMessage.cs`
- Test: `tests/ExecPlan.UnitTests/Domain/RuntimeGraphTests.cs`

**Interfaces:**
- Produces: runtime entities consumed by ActivationService, EscalationService, DashboardService.

- [ ] **Step 1: Failing test**

```csharp
public class RuntimeGraphTests {
  [Fact] public void Activation_participant_and_task_link_by_ids() {
    var act = new PlanActivation { PlanId=Guid.NewGuid(), Status=ActivationStatus.Active, Shift=ShiftBand.Morning, RosterDate=new DateTime(2026,6,30), ActivatedByUserId=Guid.NewGuid(), ActivatedAtUtc=DateTime.UtcNow, EscalationThreshold=5 };
    var p = new ActivationParticipant { ActivationId=act.Id, UserId=Guid.NewGuid(), TeamId=Guid.NewGuid(), TeamNameSnapshot="Alpha", Status=ParticipantStatus.Pending, CallAttemptCount=0 };
    var t = new ExecutionTask { ActivationId=act.Id, ParticipantId=p.Id, Title="Inspect", Order=1, Status=ExecTaskStatus.Pending, DueAtUtc=DateTime.UtcNow.AddMinutes(30) };
    t.ParticipantId.Should().Be(p.Id); act.EscalationThreshold.Should().Be(5);
  }
}
```

- [ ] **Step 2: Run — expect compile failure.**

- [ ] **Step 3: Implement** (complete code)

```csharp
public class PlanActivation : BaseEntity {
  public Guid PlanId { get; set; }
  public ActivationStatus Status { get; set; } = ActivationStatus.Active;
  public ShiftBand Shift { get; set; }
  public DateTime RosterDate { get; set; }
  public Guid ActivatedByUserId { get; set; }
  public DateTime ActivatedAtUtc { get; set; }
  public DateTime? ClosedAtUtc { get; set; }
  public int EscalationThreshold { get; set; }
}
public class ActivationParticipant : BaseEntity {
  public Guid ActivationId { get; set; }
  public Guid UserId { get; set; }
  public Guid TeamId { get; set; }
  public string TeamNameSnapshot { get; set; } = "";
  public ParticipantStatus Status { get; set; } = ParticipantStatus.Pending;
  public Guid? ResolvedSubstituteUserId { get; set; }
  public int CallAttemptCount { get; set; }
  public bool IsSubstitute { get; set; }
  public Guid? InductedFromParticipantId { get; set; }
}
public class ExecutionTask : BaseEntity {
  public Guid ActivationId { get; set; }
  public Guid ParticipantId { get; set; }
  public string Title { get; set; } = "";
  public int Order { get; set; }
  public ExecTaskStatus Status { get; set; } = ExecTaskStatus.Pending;
  public string? Note { get; set; }
  public DateTime DueAtUtc { get; set; }
  public DateTime? CompletedAtUtc { get; set; }
  public Guid SourceTaskTemplateId { get; set; }
}
public class NotificationLog : BaseEntity { public Guid ActivationId { get; set; } public Guid RecipientUserId { get; set; } public NotificationKind Kind { get; set; } public string Body { get; set; } = ""; }
public class CallAttempt : BaseEntity { public Guid ActivationId { get; set; } public Guid ParticipantId { get; set; } public int AttemptNumber { get; set; } }
public class ResponseStatus : BaseEntity { public Guid ActivationId { get; set; } public Guid ParticipantId { get; set; } public DateTime AcknowledgedAtUtc { get; set; } }
public class EscalationLog : BaseEntity { public Guid ActivationId { get; set; } public Guid ParticipantId { get; set; } public Guid SubstituteUserId { get; set; } public Guid NewParticipantId { get; set; } }
public class BroadcastMessage : BaseEntity { public Guid ActivationId { get; set; } public Guid SenderUserId { get; set; } public string Body { get; set; } = ""; }
```

- [ ] **Step 4: Run — PASS.**
- [ ] **Step 5: Commit** `feat(domain): runtime snapshot entities`

---

## Wave 2 — Infrastructure & persistence

### Task 5: DbContext, configurations, provider switch, repositories, UnitOfWork

**Files:**
- Create: `src/ExecPlan.Application/Abstractions/{IClock,IRepository,IUnitOfWork,INotificationProvider,IRealtimeNotifier,ICurrentUser}.cs` (interfaces from Locked Contracts)
- Create: `src/ExecPlan.Infrastructure/Persistence/ExecPlanDbContext.cs`, `.../Configurations/*.cs`, `.../Repository.cs`, `.../UnitOfWork.cs`, `.../RefreshToken.cs`
- Create: `src/ExecPlan.Infrastructure/DependencyInjection.cs`
- Test: `tests/ExecPlan.IntegrationTests/SqliteFixture.cs`, `tests/ExecPlan.IntegrationTests/PersistenceTests.cs`

**Interfaces:**
- Consumes: all domain entities.
- Produces: `ExecPlanDbContext` with `DbSet`s for every entity + `RefreshToken`; `Repository<T>`, `UnitOfWork`; `AddInfrastructure(IConfiguration)` registering provider by `Database:Provider`.

- [ ] **Step 1: Write the abstractions** (interfaces verbatim from Locked Contracts; `RefreshToken` infra entity below).

```csharp
// Infrastructure/Persistence/RefreshToken.cs
public class RefreshToken : BaseEntity {
  public Guid UserId { get; set; }
  public string TokenHash { get; set; } = "";
  public DateTime ExpiresAtUtc { get; set; }
  public DateTime? RevokedAtUtc { get; set; }
  public string? ReplacedByTokenHash { get; set; }
}
```

- [ ] **Step 2: Failing integration test (SQLite in-memory round-trip)**

```csharp
// SqliteFixture.cs — opens a kept-alive in-memory SQLite connection and builds the schema
public sealed class SqliteFixture : IDisposable {
  public DbConnection Connection { get; }
  public SqliteFixture() {
    Connection = new SqliteConnection("DataSource=:memory:");
    Connection.Open();
    using var ctx = NewContext();
    ctx.Database.EnsureCreated();
  }
  public ExecPlanDbContext NewContext() {
    var opts = new DbContextOptionsBuilder<ExecPlanDbContext>().UseSqlite(Connection).Options;
    return new ExecPlanDbContext(opts);
  }
  public void Dispose() => Connection.Dispose();
}

public class PersistenceTests : IClassFixture<SqliteFixture> {
  private readonly SqliteFixture _fx; public PersistenceTests(SqliteFixture fx) => _fx = fx;
  [Fact] public async Task Can_persist_and_read_back_a_plan() {
    var planId = Guid.NewGuid();
    await using (var ctx = _fx.NewContext()) { ctx.Set<Plan>().Add(new Plan { Id=planId, Name="P", Type=PlanType.Guard, CreatedByUserId=Guid.NewGuid() }); await ctx.SaveChangesAsync(); }
    await using (var ctx = _fx.NewContext()) { (await ctx.Set<Plan>().FindAsync(planId))!.Name.Should().Be("P"); }
  }
}
```

- [ ] **Step 3: Run — expect compile failure (no DbContext).**

- [ ] **Step 4: Implement `ExecPlanDbContext` + configurations**

```csharp
public class ExecPlanDbContext : DbContext {
  public ExecPlanDbContext(DbContextOptions<ExecPlanDbContext> o) : base(o) {}
  public DbSet<Organization> Organizations => Set<Organization>();
  public DbSet<Department> Departments => Set<Department>();
  public DbSet<User> Users => Set<User>();
  public DbSet<Plan> Plans => Set<Plan>();
  public DbSet<PlanContact> PlanContacts => Set<PlanContact>();
  public DbSet<PlanActivator> PlanActivators => Set<PlanActivator>();
  public DbSet<Team> Teams => Set<Team>();
  public DbSet<TeamMembership> TeamMemberships => Set<TeamMembership>();
  public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();
  public DbSet<TaskTemplate> TaskTemplates => Set<TaskTemplate>();
  public DbSet<PlanActivation> PlanActivations => Set<PlanActivation>();
  public DbSet<ActivationParticipant> ActivationParticipants => Set<ActivationParticipant>();
  public DbSet<ExecutionTask> ExecutionTasks => Set<ExecutionTask>();
  public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
  public DbSet<CallAttempt> CallAttempts => Set<CallAttempt>();
  public DbSet<ResponseStatus> ResponseStatuses => Set<ResponseStatus>();
  public DbSet<EscalationLog> EscalationLogs => Set<EscalationLog>();
  public DbSet<BroadcastMessage> BroadcastMessages => Set<BroadcastMessage>();
  public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
  protected override void OnModelCreating(ModelBuilder b) {
    b.ApplyConfigurationsFromAssembly(typeof(ExecPlanDbContext).Assembly);
    // Global: Guid keys are app-assigned (ValueGeneratedNever) so SaveChanges never re-generates them.
    foreach (var et in b.Model.GetEntityTypes()) {
      var pk = et.FindPrimaryKey()?.Properties.FirstOrDefault(p => p.Name=="Id");
      pk?.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
  }
}
```

Write one `IEntityTypeConfiguration<T>` per entity under `Configurations/`. Each: PK on `Id`, required strings with `HasMaxLength(200)` (Body/Note/Description/Scope/Objective use `HasMaxLength(2000)`), `User.UserName` unique index, useful indexes: `ShiftAssignment (TeamId, Shift, Date)`, `ActivationParticipant (ActivationId)`, `ExecutionTask (ActivationId, ParticipantId)`, `PlanActivation (PlanId, Status)`, `RefreshToken (TokenHash)` unique, `Plan.Contacts`/`Plan.Activators` as owned-by-FK child collections with cascade delete. Store enums as `int` (default). Store `TimeSpan` and `DateTime` with provider defaults. Example exemplar:

```csharp
public class UserConfig : IEntityTypeConfiguration<User> {
  public void Configure(EntityTypeBuilder<User> e) {
    e.ToTable("Users");
    e.HasKey(x => x.Id);
    e.Property(x => x.UserName).IsRequired().HasMaxLength(100);
    e.HasIndex(x => x.UserName).IsUnique();
    e.Property(x => x.FullName).IsRequired().HasMaxLength(200);
    e.Property(x => x.Phone).HasMaxLength(40);
    e.Property(x => x.PasswordHash).IsRequired();
  }
}
```

- [ ] **Step 5: Implement `Repository<T>` and `UnitOfWork`**

```csharp
public class Repository<T> : IRepository<T> where T : BaseEntity {
  private readonly ExecPlanDbContext _db; public Repository(ExecPlanDbContext db) => _db = db;
  public Task<T?> GetByIdAsync(Guid id, CancellationToken ct=default) => _db.Set<T>().FirstOrDefaultAsync(x => x.Id==id, ct);
  public IQueryable<T> Query() => _db.Set<T>().AsNoTracking();
  public IQueryable<T> Tracking() => _db.Set<T>();
  public async Task AddAsync(T e, CancellationToken ct=default) => await _db.Set<T>().AddAsync(e, ct);
  public async Task AddRangeAsync(IEnumerable<T> e, CancellationToken ct=default) => await _db.Set<T>().AddRangeAsync(e, ct);
  public void Remove(T e) => _db.Set<T>().Remove(e);
}
public class UnitOfWork : IUnitOfWork {
  private readonly ExecPlanDbContext _db; public UnitOfWork(ExecPlanDbContext db) => _db = db;
  public IRepository<T> Repo<T>() where T : BaseEntity => new Repository<T>(_db);
  public Task<int> SaveChangesAsync(CancellationToken ct=default) => _db.SaveChangesAsync(ct);
  public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct=default) {
    if (_db.Database.IsRelational() && _db.Database.ProviderName?.Contains("Sqlite") != true)
      return await _db.Database.BeginTransactionAsync(ct);
    return new NoopTx(); // Sqlite in-memory shares one connection; rely on SaveChanges atomicity in tests
  }
  private sealed class NoopTx : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}
```

- [ ] **Step 6: Implement `AddInfrastructure(IConfiguration)`**

```csharp
public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration cfg) {
  var provider = cfg["Database:Provider"] ?? "SqlServer";
  s.AddDbContext<ExecPlanDbContext>(o => {
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)) o.UseSqlite(cfg.GetConnectionString("Default"));
    else o.UseSqlServer(cfg.GetConnectionString("Default"));
  });
  s.AddScoped<IUnitOfWork, UnitOfWork>();
  s.AddScoped(typeof(IRepository<>), typeof(Repository<>));
  s.AddSingleton<IClock, KuwaitClock>();
  s.AddScoped<INotificationProvider, DatabasePlaceholderProvider>();
  // Auth services registered in Task 8/9
  return s;
}
```

- [ ] **Step 7: Run the persistence test — PASS.**
- [ ] **Step 8: Set `CreatedAtUtc` on insert.** Override `SaveChangesAsync` in the context to stamp `CreatedAtUtc`/`UpdatedAtUtc` from `DateTime.UtcNow` for `Added`/`Modified` `BaseEntity` entries. Add a test asserting `CreatedAtUtc != default` after save. Run — PASS.
- [ ] **Step 9: Commit** `feat(infra): EF Core DbContext, configs, repository/UoW, provider switch`

### Task 6: KuwaitClock + first SQL Server migration

**Files:**
- Create: `src/ExecPlan.Infrastructure/Time/KuwaitClock.cs`
- Create: `src/ExecPlan.Infrastructure/Persistence/Migrations/*` (generated)

- [ ] **Step 1: Implement `KuwaitClock`**

```csharp
public sealed class KuwaitClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }
```

- [ ] **Step 2: Generate the initial migration (SqlServer)**

```bash
dotnet tool install --global dotnet-ef --version 9.* || dotnet tool update --global dotnet-ef --version 9.*
dotnet ef migrations add InitialCreate -p src/ExecPlan.Infrastructure -s src/ExecPlan.Api -o Persistence/Migrations
```
Expected: a migration is created referencing all tables. (Provider for design-time = SqlServer; ensure `appsettings.Development.json` has the dev conn string OR add a `DesignTimeDbContextFactory` defaulting to SqlServer.)

- [ ] **Step 3: Add `DesignTimeDbContextFactory`** so EF tooling never needs the running app's DI:

```csharp
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExecPlanDbContext> {
  public ExecPlanDbContext CreateDbContext(string[] args) {
    var cfg = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional:true)
      .AddJsonFile("appsettings.Development.json", optional:true).AddEnvironmentVariables().Build();
    var o = new DbContextOptionsBuilder<ExecPlanDbContext>().UseSqlServer(cfg.GetConnectionString("Default") ?? "Server=.;Database=Exec-Plan;Trusted_Connection=True;TrustServerCertificate=True").Options;
    return new ExecPlanDbContext(o);
  }
}
```

- [ ] **Step 4: Build — PASS. Commit** `feat(infra): KuwaitClock + InitialCreate migration`

> Applying the migration to the real DB (`dotnet ef database update`) happens in Task 20 (seeding/run), not here.

---

## Wave 3 — Auth

### Task 7: Password hashing + JWT factory + AuthService (login/refresh/rotation)

**Files:**
- Create: `src/ExecPlan.Application/Auth/{IAuthService,AuthService,TokenModels,IJwtTokenFactory,IPasswordHasher}.cs`
- Create: `src/ExecPlan.Infrastructure/Auth/{JwtTokenFactory,IdentityPasswordHasher,RefreshTokenStore}.cs`
- Test: `tests/ExecPlan.IntegrationTests/AuthServiceTests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`, `User`, `RefreshToken`, `IClock`.
- Produces: `IAuthService` (Locked Contracts), `IPasswordHasher { string Hash(string); bool Verify(string hash, string pw); }`, `IJwtTokenFactory { (string token, DateTime expUtc) Create(AppUserPrincipal u); }`.

- [ ] **Step 1: Failing tests**

```csharp
public class AuthServiceTests : IClassFixture<SqliteFixture> {
  // Arrange a seeded user with a known hashed password via the real IPasswordHasher.
  [Fact] public async Task Login_with_valid_credentials_returns_tokens() { /* seed user, call LoginAsync, assert AccessToken non-empty + Role */ }
  [Fact] public async Task Login_with_wrong_password_throws_Forbidden() { /* assert AppException Unauthorized */ }
  [Fact] public async Task Refresh_rotates_token_and_revokes_old() { /* login, refresh, old refresh token now revoked, new differs */ }
  [Fact] public async Task Refresh_with_revoked_or_unknown_token_throws() { }
}
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement `IdentityPasswordHasher`** (wraps `PasswordHasher<object>`), `JwtTokenFactory` (HS256 over `Jwt:SigningKey`, claims: sub=UserId, role, name), and `AuthService`:
  - `LoginAsync`: find active user by `UserName`; `Verify`; on fail throw `AppException.Unauthorized`. Create access token; create refresh token (random 32 bytes base64url), store **hash** in `RefreshToken` with `ExpiresAtUtc = UtcNow + RefreshTokenDays`; return `TokenPair`.
  - `RefreshAsync`: hash incoming token, find non-revoked unexpired `RefreshToken`; if none → throw; mark old revoked + `ReplacedByTokenHash`; issue new pair; one `SaveChanges`.
  - `ValidateCredentialsAsync`: same credential check, returns `AppUserPrincipal` for cookie sign-in.

- [ ] **Step 4: Register auth services** in `AddInfrastructure`: `IPasswordHasher`, `IJwtTokenFactory`, `IAuthService`.

- [ ] **Step 5: Run — PASS. Commit** `feat(auth): password hashing, JWT factory, login/refresh rotation`

### Task 8: Wire cookie + JWT schemes and role policies in the Api host

**Files:**
- Create: `src/ExecPlan.Api/Program.cs` (auth + DI wiring), `src/ExecPlan.Api/Auth/{CurrentUser,AuthPolicies}.cs`
- Test: `tests/ExecPlan.IntegrationTests/TestAppFactory.cs`, `.../AuthEndpointTests.cs`

**Interfaces:**
- Consumes: `AddInfrastructure`, `AddApplication`, `IAuthService`.
- Produces: running host; `ICurrentUser` from `HttpContext`; policies `Admin`, `Manager`, `Leader`, `Member`, `ManagerOrAdmin`.

- [ ] **Step 1: Failing test** — `TestAppFactory` (WebApplicationFactory) overrides config to `Database:Provider=Sqlite` with a shared open connection; seeds one admin. Then:

```csharp
[Fact] public async Task Protected_endpoint_returns_401_without_token() { var c=_factory.CreateClient(); (await c.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized); }
[Fact] public async Task Login_then_call_users_as_admin_returns_200() { /* POST /auth/login, set Bearer, GET /users => 200 */ }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement `Program.cs`**: `AddInfrastructure(cfg)` + `AddApplication()`; `AddControllers()` + `AddControllersWithViews()`; cookie scheme `AdminCookie` (LoginPath `/admin/login`) + JWT bearer (validate issuer/audience/signing key; **also read token from `access_token` query for `/hubs` paths**); `AddAuthorization` with the five policies mapping to `UserRole` claims; `AddSignalR()`; `AddHttpContextAccessor` + `ICurrentUser`. Map controllers, hub (`/hubs/dashboard`), and the Admin area route. Add `RequestLocalization` (Task 19 fills detail; stub default `ar` now).

- [ ] **Step 4: Run — PASS. Commit** `feat(api): cookie+JWT auth, role policies, host wiring`

---

## Wave 4 — Domain services (the core)

### Task 9: KuwaitShiftCalculator

**Files:**
- Create: `src/ExecPlan.Application/Shifts/{KuwaitShiftCalculator,ShiftResolution}.cs`
- Test: `tests/ExecPlan.UnitTests/Shifts/KuwaitShiftCalculatorTests.cs`

**Interfaces:**
- Produces: `ShiftResolution(ShiftBand Band, DateTime RosterDate)`; `KuwaitShiftCalculator.Resolve(DateTime utcNow) : ShiftResolution`.

- [ ] **Step 1: Failing tests** (convert Kuwait local → assert band + roster date)

```csharp
public class KuwaitShiftCalculatorTests {
  private static DateTime KwtToUtc(int y,int mo,int d,int h,int mi){ var tz=TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait"); return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y,mo,d,h,mi,0,DateTimeKind.Unspecified), tz); }
  private readonly KuwaitShiftCalculator _c = new();
  [Fact] public void Morning_0900_is_Morning_today()  { var r=_c.Resolve(KwtToUtc(2026,6,30,9,0));  r.Band.Should().Be(ShiftBand.Morning); r.RosterDate.Should().Be(new DateTime(2026,6,30)); }
  [Fact] public void Evening_1500_is_Evening_today()  { var r=_c.Resolve(KwtToUtc(2026,6,30,15,0)); r.Band.Should().Be(ShiftBand.Evening); r.RosterDate.Should().Be(new DateTime(2026,6,30)); }
  [Fact] public void Night_2300_is_Night_today()      { var r=_c.Resolve(KwtToUtc(2026,6,30,23,0)); r.Band.Should().Be(ShiftBand.Night); r.RosterDate.Should().Be(new DateTime(2026,6,30)); }
  [Fact] public void Night_0200_belongs_to_prev_day() { var r=_c.Resolve(KwtToUtc(2026,7,1,2,0));   r.Band.Should().Be(ShiftBand.Night); r.RosterDate.Should().Be(new DateTime(2026,6,30)); }
  [Fact] public void Boundary_0600_is_Morning()       { _c.Resolve(KwtToUtc(2026,6,30,6,0)).Band.Should().Be(ShiftBand.Morning); }
  [Fact] public void Boundary_2200_is_Night()         { _c.Resolve(KwtToUtc(2026,6,30,22,0)).Band.Should().Be(ShiftBand.Night); }
}
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement**

```csharp
public sealed record ShiftResolution(ShiftBand Band, DateTime RosterDate);
public sealed class KuwaitShiftCalculator {
  private static readonly TimeZoneInfo Tz = ResolveTz();
  private static TimeZoneInfo ResolveTz() { try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait"); } catch { return TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time"); } }
  public ShiftResolution Resolve(DateTime utcNow) {
    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Tz);
    int h = local.Hour;
    if (h >= 6 && h < 14)  return new(ShiftBand.Morning, local.Date);
    if (h >= 14 && h < 22) return new(ShiftBand.Evening, local.Date);
    // Night 22:00–06:00: after-midnight (h < 6) belongs to previous day
    var rosterDate = h < 6 ? local.Date.AddDays(-1) : local.Date;
    return new(ShiftBand.Night, rosterDate);
  }
}
```

- [ ] **Step 4: Run — PASS. Commit** `feat(app): KuwaitShiftCalculator with night-after-midnight rule`

### Task 10: AppException + DatabasePlaceholderProvider

**Files:**
- Create: `src/ExecPlan.Application/Common/AppException.cs`
- Create: `src/ExecPlan.Infrastructure/Notifications/DatabasePlaceholderProvider.cs`
- Test: `tests/ExecPlan.UnitTests/Common/AppExceptionTests.cs`

- [ ] **Step 1:** Implement `AppException` with factory helpers + a `Kind` enum `{ NotFound, Forbidden, Unauthorized, Conflict, Validation }`. Test each factory sets the kind. Implement `DatabasePlaceholderProvider`:

```csharp
public sealed class DatabasePlaceholderProvider : INotificationProvider {
  private readonly IUnitOfWork _uow;
  public DatabasePlaceholderProvider(IUnitOfWork uow) => _uow = uow;
  public NotificationLog StageNotification(Guid actId, Guid recipient, NotificationKind kind, string body, DateTime utcNow) {
    var n = new NotificationLog { ActivationId=actId, RecipientUserId=recipient, Kind=kind, Body=body, CreatedAtUtc=utcNow };
    _uow.Repo<NotificationLog>().AddAsync(n).GetAwaiter().GetResult(); return n;
  }
  public CallAttempt StageCallAttempt(Guid actId, Guid pid, int n, DateTime utcNow) {
    var c = new CallAttempt { ActivationId=actId, ParticipantId=pid, AttemptNumber=n, CreatedAtUtc=utcNow };
    _uow.Repo<CallAttempt>().AddAsync(c).GetAwaiter().GetResult(); return c;
  }
}
```

- [ ] **Step 2: Commit** `feat(app/infra): AppException + DatabasePlaceholderProvider seam`

### Task 11: ActivationService

**Files:**
- Create: `src/ExecPlan.Application/Activation/{IActivationService,ActivationService}.cs`
- Test: `tests/ExecPlan.IntegrationTests/ActivationServiceTests.cs`

**Interfaces:**
- Consumes: `IUnitOfWork`, `IClock`, `KuwaitShiftCalculator`, `INotificationProvider`, `IRealtimeNotifier`, config threshold.
- Produces: `IActivationService.ActivateAsync` (Locked Contracts).

- [ ] **Step 1: Failing tests** (seed plan/team/template/roster + substitute via `SqliteFixture`):

```csharp
[Fact] public async Task Activate_creates_snapshot_participants_tasks_and_first_call() { /* one on-duty user => 1 participant, N tasks=template count, 1 notification, 1 call attempt#1, participant.CallAttemptCount==1, status Pending */ }
[Fact] public async Task Activate_freezes_substitute_from_roster() { /* roster substitute row => participant.ResolvedSubstituteUserId set */ }
[Fact] public async Task Activate_rejects_when_already_active() { /* second activate => AppException.Conflict */ }
[Fact] public async Task Activate_rejects_when_no_one_on_duty() { /* empty roster => AppException.Validation/Conflict */ }
[Fact] public async Task Activate_rejects_unauthorized_user() { /* non-creator/non-activator/non-admin => Forbidden */ }
[Fact] public async Task Task_due_time_is_activated_at_plus_duration() { }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** `ActivateAsync` exactly per spec §5.3 (guards → resolve shift → snapshot participants → freeze substitute → generate tasks `DueAtUtc=now+Duration` → stage notification + call#1 → `CallAttemptCount=1` → one `SaveChanges` inside `BeginTransactionAsync` → after commit `IRealtimeNotifier.DashboardChangedAsync`). On-duty rows = `ShiftAssignment` where `Shift==band && Date==rosterDate && SubstituteForUserId==null`; substitute for a user = a `ShiftAssignment` row with `SubstituteForUserId==userId` (same team/shift/date). Authorization: actor is `Plan.CreatedByUserId` OR a `PlanActivator.UserId` OR role `SystemAdmin`. Reject "already active" if any `PlanActivation` for the plan has `Status==Active`.

- [ ] **Step 4: Run — PASS. Commit** `feat(app): ActivationService (snapshot, tasks, notify, guards)`

### Task 12: AcknowledgeService

**Files:** Create `src/ExecPlan.Application/Execution/AcknowledgeService.cs`; Test `.../IntegrationTests/AcknowledgeTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Acknowledge_sets_ready_and_writes_response() { /* participant Status==Ready, one ResponseStatus row */ }
[Fact] public async Task Acknowledge_is_idempotent() { /* twice => still one ResponseStatus, still Ready */ }
[Fact] public async Task Acknowledge_by_non_participant_throws_Forbidden() { }
```

- [ ] **Step 2-4:** Implement `AcknowledgeAsync(activationId, actingUserId)`: find participant by `(ActivationId, UserId)`; if none → Forbidden; if no existing `ResponseStatus` → add one (`AcknowledgedAtUtc=clock`), set `Status=Ready`; save; realtime push. Run — PASS. Commit `feat(app): AcknowledgeService (the one counted response)`.

### Task 13: EscalationService

**Files:** Create `src/ExecPlan.Application/Escalation/{IEscalationService,EscalationService}.cs`; Test `.../IntegrationTests/EscalationServiceTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Cycle_adds_one_attempt_per_pending_participant() { /* 2 pending => +2 CallAttempt, CallAttemptCount incremented */ }
[Fact] public async Task At_threshold_non_responder_is_escalated_and_substitute_inducted() {
  /* threshold=2; pending participant w/ ResolvedSubstitute; after enough cycles to reach 2 => participant.Status==Escalated,
     a new ActivationParticipant exists (IsSubstitute, InductedFromParticipantId set, Status Inducted->Pending),
     substitute has full task set, a NotificationLog, CallAttempt#1, and an EscalationLog row. */
}
[Fact] public async Task Ready_participant_is_not_escalated() { }
[Fact] public async Task Cli_and_service_paths_are_identical() { /* covered by invoking same service in Task 18 */ }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** per spec §5.4. Inside one transaction: load Active activation (else Conflict/NotFound); for each participant with `Status==Pending`: stage `CallAttempt` (number = `++CallAttemptCount`); if `CallAttemptCount >= activation.EscalationThreshold` → set `Status=Escalated`, and if `ResolvedSubstituteUserId` present and not already inducted: create substitute participant (`IsSubstitute=true`, `InductedFromParticipantId=p.Id`, `Status=Pending`), generate its task set (copy titles/orders/due from the escalated participant's tasks OR regenerate from the same team templates with `DueAt=now+Duration` — use the team templates), stage notification + call#1 (`CallAttemptCount=1`), write `EscalationLog`. One `SaveChanges`; after commit realtime push. Return `EscalationCycleResult`.

- [ ] **Step 4: Run — PASS. Commit** `feat(app): EscalationService (cycle + substitute induction)`

### Task 14: DashboardService

**Files:** Create `src/ExecPlan.Application/Dashboard/{IDashboardService,DashboardService,DashboardDto}.cs`; Test `.../IntegrationTests/DashboardServiceTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Snapshot_counts_participants_by_status() { /* totals match */ }
[Fact] public async Task Response_and_completion_rates_are_correct() { /* ready/total, done/total tasks */ }
[Fact] public async Task Teams_sorted_best_to_delayed() { /* higher score first */ }
[Fact] public async Task Overdue_lists_pending_tasks_past_due() { }
[Fact] public async Task Events_capped_at_50_and_newest_first() { }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** `GetSnapshotAsync` per spec §5.5. Load participants, tasks, and the six log sources for the activation (use separate `AsNoTracking` queries or `Include(...).AsSplitQuery()`). Counters from participant `Status`. `ResponseRate = Ready/Total`. `TaskCompletionRate = Done/TasksTotal`. Team rows grouped by `TeamId`/`TeamNameSnapshot`; `Score = 0.5*readyRatio + 0.5*doneRatio`; order by `Score` desc. Overdue = tasks `Status==Pending && DueAtUtc < clock.UtcNow`. Events = union of (notification, call, response, escalation, task-completed, broadcast) projected to `FeedEvent(AtUtc,Type,Text)`, `OrderByDescending(AtUtc).Take(50)`.

- [ ] **Step 4: Run — PASS. Commit** `feat(app): DashboardService aggregate (counts, rates, ranking, overdue, feed)`

### Task 15: ExecutionService (task update/reassign, set-substitute, raise-issue) + BroadcastService + Close

**Files:** Create `src/ExecPlan.Application/Execution/ExecutionService.cs`, `src/ExecPlan.Application/Broadcast/BroadcastService.cs`; Test `.../IntegrationTests/ExecutionAndBroadcastTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Member_marks_task_done_with_note() { }
[Fact] public async Task Member_cannot_update_another_members_task() { /* Forbidden */ }
[Fact] public async Task Leader_reassign_within_led_team_succeeds() { }
[Fact] public async Task Leader_reassign_across_team_boundary_is_Forbidden() { /* 403 */ }
[Fact] public async Task Reassign_to_participant_of_other_activation_is_rejected() { /* Validation */ }
[Fact] public async Task Set_substitute_live_updates_resolved_substitute() { }
[Fact] public async Task Broadcast_creates_message_and_one_notification_per_participant() { }
[Fact] public async Task Close_sets_status_closed_and_returns_counts() { }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement**
  - `UpdateTaskAsync(taskId, done?, note?, reassignToParticipantId?, ICurrentUser)`: load task; permission = owner member (own task), or leader of the owner's team, or manager/admin. For `reassignToParticipantId`: target participant must be in the **same activation** (else Validation) and, if actor is a leader, both source and target participants' `TeamId` must be a team the leader leads (else Forbidden). Apply done→`CompletedAtUtc`, note, owner change. Save + realtime push.
  - `SetSubstituteLiveAsync(activationId, participantId, substituteUserId, ICurrentUser)`: leader(of team)/manager/admin; update `ResolvedSubstituteUserId`. Save.
  - `RaiseIssueAsync(activationId, body, ICurrentUser)`: leader only; record as a `NotificationLog`(kind=Notification, recipient=manager/creator) so it surfaces in the feed. Save + push.
  - `BroadcastService.BroadcastAsync(activationId, body, ICurrentUser)`: manager/admin; one `BroadcastMessage` + one `NotificationLog`(kind=Broadcast) per participant; one transaction; push.
  - `CloseAsync(activationId, ICurrentUser)`: manager/admin; set `Status=Closed`, `ClosedAtUtc`; save; `IRealtimeNotifier.ActivationClosedAsync`. Return final counts (reuse `DashboardService` totals).

- [ ] **Step 4: Run — PASS. Commit** `feat(app): execution/leader ops, broadcast, close (object-level checks)`

### Task 16: AddApplication() DI + remaining service registrations

**Files:** Create `src/ExecPlan.Application/DependencyInjection.cs`.

- [ ] **Step 1:** Register `KuwaitShiftCalculator`, `IAuthService`, `IActivationService`, `AcknowledgeService`, `IEscalationService`, `IDashboardService`, `ExecutionService`, `BroadcastService` as scoped. Build the solution. Commit `feat(app): AddApplication DI registrations`.

---

## Wave 5 — API surface, realtime, CLI, seed, localization

### Task 17: Auth + CRUD controllers (role-gated, role-filtered)

**Files:** Create `src/ExecPlan.Api/Controllers/AuthController.cs` + CRUD controllers for organizations, departments, users, plans, teams, team-members, shift-assignments, task-templates. Test `.../IntegrationTests/CrudAndVisibilityTests.cs`.

**Interfaces:**
- Consumes: `IAuthService`, `IUnitOfWork`, `ICurrentUser`.
- Produces: REST routes under `/api/v1/...`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Login_returns_token_and_role() { }
[Fact] public async Task Manager_can_read_users_but_not_create() { /* GET 200, POST 403 (FR-ADM-3/4) */ }
[Fact] public async Task Admin_can_create_user() { }
[Fact] public async Task Member_listing_plans_sees_none_or_forbidden() { /* role filter */ }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** `AuthController` (`POST /api/v1/auth/login`, `/auth/refresh`). Implement CRUD controllers with `[Authorize(Policy="Admin")]` on writes; reads allow Admin+Manager (`ManagerOrAdmin`) per the §14 matrix (users/departments/orgs read-only for managers). Use the generic `IRepository<T>` for simple CRUD. One **complete exemplar** (`UsersController`) below; the rest follow the identical pattern with their own entity + DTO:

```csharp
[ApiController, Route("api/v1/users")]
public class UsersController : ControllerBase {
  private readonly IUnitOfWork _uow; private readonly IPasswordHasher _hash;
  public UsersController(IUnitOfWork uow, IPasswordHasher hash){ _uow=uow; _hash=hash; }
  [HttpGet, Authorize(Policy="ManagerOrAdmin")]
  public async Task<IActionResult> List() => Ok(await _uow.Repo<User>().Query().Select(u => new { u.Id, u.UserName, u.FullName, u.Phone, u.Role, u.DepartmentId }).ToListAsync());
  [HttpPost, Authorize(Policy="Admin")]
  public async Task<IActionResult> Create([FromBody] CreateUserDto dto) {
    var u = new User { UserName=dto.UserName, FullName=dto.FullName, Phone=dto.Phone, Role=dto.Role, OrganizationId=dto.OrganizationId, DepartmentId=dto.DepartmentId, PasswordHash=_hash.Hash(dto.Password) };
    await _uow.Repo<User>().AddAsync(u); await _uow.SaveChangesAsync(); return Ok(new { u.Id });
  }
}
public record CreateUserDto(string UserName, string Password, string FullName, string Phone, UserRole Role, Guid OrganizationId, Guid? DepartmentId);
```
Enumerate the remaining controllers with their write-DTOs: Organizations(Name), Departments(Name,OrganizationId), Plans(Name,Type,Objective,Description,Scope + nested Contacts/Activators; writes=Admin/Manager), Teams(PlanId,Name,TeamLeaderUserId), TeamMembers(TeamId,UserId), ShiftAssignments(TeamId,UserId,Shift,Date,SubstituteForUserId), TaskTemplates(TeamId,Title,Order,Duration). Plans/Teams/etc. writes use policy `ManagerOrAdmin`.

- [ ] **Step 4: Run — PASS. Commit** `feat(api): auth + CRUD controllers (role gates, role-filtered reads)`

### Task 18: Activation/execution/dashboard controllers + full acceptance flow

**Files:** Create `src/ExecPlan.Api/Controllers/{ActivationsController,ExecutionTasksController,PlansActivateController}.cs`. Test `.../IntegrationTests/AcceptanceFlowTests.cs` (the PRD §21 scenario end-to-end).

- [ ] **Step 1: Failing acceptance test** (the headline test)

```csharp
[Fact] public async Task Full_activation_cycle_meets_PRD_section_21() {
  // seed plan w/ 2 members + substitute; login as manager; POST /plans/{id}/activate
  // login as member1; POST /activations/{id}/acknowledge
  // GET /activations/{id}/dashboard => Ready==1, Pending==1
  // POST /activations/{id}/run-escalation (threshold small) until member2 escalated => substitute inducted
  // member-only visibility: GET /activations/{id}/my-tasks as member1 returns only member1 tasks
  // POST /activations/{id}/close => status Closed
}
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** the endpoints, each delegating to the matching service and returning its DTO:
  `POST /api/v1/plans/{id}/activate` → `IActivationService` (policy Manager/Admin, also checks authorized-activator inside service);
  `GET /api/v1/activations/{id}/dashboard` → `IDashboardService` (Manager/Admin/Leader-own);
  `POST /api/v1/activations/{id}/acknowledge` → `AcknowledgeService` (Member/Leader);
  `POST /api/v1/activations/{id}/run-escalation` → `IEscalationService` (Manager/Admin);
  `PATCH /api/v1/execution-tasks/{id}` → `ExecutionService.UpdateTaskAsync`;
  `POST /api/v1/activations/{id}/broadcast` → `BroadcastService`;
  `POST /api/v1/activations/{id}/set-substitute` → `ExecutionService.SetSubstituteLiveAsync`;
  `POST /api/v1/activations/{id}/raise-issue` → `ExecutionService.RaiseIssueAsync`;
  `GET /api/v1/activations/{id}/my-tasks` + `/my-notifications` (filtered to `ICurrentUser`);
  `POST /api/v1/activations/{id}/close` → `ExecutionService.CloseAsync`.

- [ ] **Step 4: Run — PASS (this is the core acceptance gate). Commit** `feat(api): activation/execution/dashboard endpoints + acceptance flow`

### Task 19: SignalR DashboardHub + IRealtimeNotifier + localization wiring

**Files:** Create `src/ExecPlan.Api/Hubs/DashboardHub.cs`, `src/ExecPlan.Api/Hubs/SignalRRealtimeNotifier.cs`, `src/ExecPlan.Api/Localization/*`. Test `.../IntegrationTests/RealtimeAndLocalizationTests.cs`.

- [ ] **Step 1: Failing tests**

```csharp
[Fact] public async Task Realtime_notifier_pushes_dashboard_on_acknowledge() { /* test double or SignalR test client receives "DashboardUpdated" group message after ack */ }
[Fact] public async Task Default_culture_is_arabic_and_cookie_switches_to_en() { /* GET with no cookie => ar; POST /set-language?culture=en sets cookie; subsequent request => en */ }
```

- [ ] **Step 2: Run — expect failure.**

- [ ] **Step 3: Implement** `DashboardHub` (`[Authorize]`, methods `JoinActivation(Guid id)`/`LeaveActivation` adding to group `act-{id}` after a visibility check); `SignalRRealtimeNotifier : IRealtimeNotifier` over `IHubContext<DashboardHub>` computing the slice via `IDashboardService` and calling `Clients.Group($"act-{id}").SendAsync("DashboardUpdated", dto)` (and `"ActivationClosed"`). Register it (replace any default `IRealtimeNotifier`). Configure JWT to accept `access_token` query on `/hubs/*`. Implement `RequestLocalizationOptions` (cultures `ar`,`en`; default `ar`; cookie provider first) and a `POST /set-language` endpoint writing the culture cookie; set `dir` in a layout/_ViewStart for the Admin area.

- [ ] **Step 4: Run — PASS. Commit** `feat(api): SignalR DashboardHub, realtime notifier, ar/en localization`

### Task 20: ExecPlan.Cli run-escalation

**Files:** Create `src/ExecPlan.Cli/Program.cs`. Test `.../IntegrationTests/CliEscalationTests.cs`.

- [ ] **Step 1: Failing test** — invoke the CLI's command handler (extract a `Runner` class so it's testable without spawning a process) against the SQLite fixture; assert it runs an escalation cycle identical to the service path.

- [ ] **Step 2-4:** Implement a generic-host console that builds DI via `AddInfrastructure`+`AddApplication`, parses `run-escalation --activation <guid>` (and `--all-active` iterating Active activations), resolves `IEscalationService`, runs the cycle, prints the `EscalationCycleResult`. Run — PASS. Commit `feat(cli): run-escalation via shared EscalationService`.

### Task 21: DataSeeder + apply migration + manual run verification

**Files:** Create `src/ExecPlan.Infrastructure/Seed/DataSeeder.cs`; wire a `--seed` path / dev-startup seed in `Program.cs`. Test `.../IntegrationTests/SeedTests.cs`.

- [ ] **Step 1: Failing test** — after `DataSeeder.SeedAsync`, assert: one admin/manager/leader/member user exist (known usernames+passwords), one showcase `Plan` with 2 teams, task templates, and a roster including a substitute; and the full acceptance flow (Task 18) runs green against the seeded data.

- [ ] **Step 2-3:** Implement idempotent `SeedAsync` (guard on "any users exist"). Seed users with `IPasswordHasher`. Build the showcase storm-response plan. Gate behind config/flag so it only runs in Development/eval.

- [ ] **Step 4: Apply the migration to the real database and smoke-run** (manual, documented):

```bash
# uses appsettings.Development.json connection string (real sa password, not committed)
dotnet ef database update -p src/ExecPlan.Infrastructure -s src/ExecPlan.Api
dotnet run --project src/ExecPlan.Api    # then hit /api/v1/auth/login with seeded manager
```
Expected: tables created in `Exec-Plan`; login works; activating the seeded plan returns an activation id.

- [ ] **Step 5: Run tests — PASS. Commit** `feat(infra): idempotent seed (4 roles + showcase plan) + migration applied`

### Task 22: Final verification sweep + PROGRESS/DECISIONS update

- [ ] **Step 1:** `dotnet build` clean; `dotnet test` all green; capture counts.
- [ ] **Step 2:** Update `docs/PROGRESS.md` (Phase-1 tasks done, dated, with commit SHAs) and confirm no new `DECISIONS.md` deviations (or record any).
- [ ] **Step 3: Commit** `docs: Phase-1 backend spine complete — progress log`.

---

## Self-Review (author checklist — completed)

**Spec coverage:** Auth (T7-8,17), 15 entities + RefreshToken (T2-5), EF/provider switch/migration (T5-6), shift logic (T9), activation (T11), acknowledge (T12), escalation+substitute (T13), dashboard (T14), execution/leader/broadcast/close + object-level checks (T15), API surface incl. all PRD §12 endpoints (T17-18), SignalR + realtime seam (T19), CLI (T20), seeding (T21), localization ar/en (T19), atomicity/provider/snapshot invariants (woven), PRD §21 acceptance flow (T18,21). No spec section left without a task.

**Placeholder scan:** No "TBD/TODO/handle edge cases" steps. Repetitive CRUD given as one complete exemplar + explicit per-entity field enumeration (engineering-justified at plan scale, not a placeholder).

**Type consistency:** `IActivationService.ActivateAsync`, `IEscalationService.RunCycleAsync`→`EscalationCycleResult`, `IDashboardService.GetSnapshotAsync`→`DashboardDto`, `IRepository<T>.Query()/Tracking()`, `INotificationProvider.StageNotification/StageCallAttempt`, `ParticipantStatus{Pending,Ready,Escalated,Inducted}`, `ExecTaskStatus{Pending,Done}` — names used consistently in every consuming task.
