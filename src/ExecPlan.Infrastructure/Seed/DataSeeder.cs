using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.Infrastructure.Seed;

/// <summary>
/// Idempotent dev/eval seed (Task 21). Guarded on "any <see cref="User"/> already exists" — re-running
/// it (every app start in Development, per the gated wiring in <c>ExecPlan.Api/Program.cs</c>) is a
/// safe no-op once the data is there. Seeds exactly one of each role with KNOWN credentials so a fresh
/// checkout can log in and exercise the product immediately, plus a single showcase storm-response
/// <see cref="Plan"/> (PRD §1's "ideal first case") structurally complete enough to drive the §21
/// acceptance flow (activate → acknowledge → escalate → induct a substitute) on first boot, regardless
/// of what time of day that boot happens — the on-duty roster is written against WHATEVER Kuwait shift
/// band/roster-date <see cref="IClock"/> resolves to right now, not a fixed date.
/// </summary>
public static class DataSeeder
{
    /// <summary>
    /// Demo-only password for all four seeded accounts. Not a production secret — this is dev/eval
    /// seed data (CLAUDE.md convention 1 governs real secrets, which this is not); the value is
    /// intentionally obvious so nobody mistakes it for a real credential.
    /// </summary>
    public const string DemoPassword = "Passw0rd!";

    public const string AdminUserName = "admin";
    public const string ManagerUserName = "manager";
    public const string LeaderUserName = "leader";
    public const string MemberUserName = "member";
    private const string SubstituteUserName = "substitute";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var uow = sp.GetRequiredService<IUnitOfWork>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();
        var clock = sp.GetRequiredService<IClock>();
        var shiftCalc = sp.GetRequiredService<KuwaitShiftCalculator>();

        // Idempotent guard: any user at all means this has already run.
        var alreadySeeded = await uow.Repo<User>().FirstOrDefaultAsync(_ => true, ct) is not null;
        if (alreadySeeded)
        {
            return;
        }

        var passwordHash = hasher.Hash(DemoPassword);

        var org = new Organization { Name = "بلدية الكويت — العمليات التجريبية" };
        await uow.Repo<Organization>().AddAsync(org, ct);

        var roadsDept = new Department { Name = "إدارة الطرق", OrganizationId = org.Id };
        var drainageDept = new Department { Name = "إدارة الصرف الصحي", OrganizationId = org.Id };
        await uow.Repo<Department>().AddRangeAsync([roadsDept, drainageDept], ct);

        var admin = new User
        {
            UserName = AdminUserName, PasswordHash = passwordHash, FullName = "مدير النظام",
            Phone = "+96500000001", Role = UserRole.SystemAdmin, OrganizationId = org.Id, IsActive = true,
        };
        var manager = new User
        {
            UserName = ManagerUserName, PasswordHash = passwordHash, FullName = "مدير الخطة",
            Phone = "+96500000002", Role = UserRole.PlanManager, OrganizationId = org.Id,
            DepartmentId = roadsDept.Id, IsActive = true,
        };
        var leader = new User
        {
            UserName = LeaderUserName, PasswordHash = passwordHash, FullName = "قائد الفريق",
            Phone = "+96500000003", Role = UserRole.TeamLeader, OrganizationId = org.Id,
            DepartmentId = roadsDept.Id, IsActive = true,
        };
        var member = new User
        {
            UserName = MemberUserName, PasswordHash = passwordHash, FullName = "عضو الفريق",
            Phone = "+96500000004", Role = UserRole.TeamMember, OrganizationId = org.Id,
            DepartmentId = roadsDept.Id, IsActive = true,
        };
        var substitute = new User
        {
            UserName = SubstituteUserName, PasswordHash = passwordHash, FullName = "البديل المجمد",
            Phone = "+96500000005", Role = UserRole.TeamMember, OrganizationId = org.Id,
            DepartmentId = drainageDept.Id, IsActive = true,
        };
        await uow.Repo<User>().AddRangeAsync([admin, manager, leader, member, substitute], ct);

        // The showcase storm-response plan (PRD §1's "ideal first case"), created by `manager` and
        // Ready so it can be activated immediately.
        var plan = new Plan
        {
            Name = "خطة الاستجابة لهطول الأمطار",
            Type = PlanType.Emergency,
            Objective = "تعبئة فرق الطرق والصرف الصحي خلال هطول أمطار غزيرة.",
            Description = "خطة عرض توضيحية: فحص مضخات الصرف وإغلاق الطرق المتأثرة عند تفعيلها.",
            Scope = "مناطق الطرق الرئيسية ومحطات الضخ التابعة للبلدية.",
            Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        await uow.Repo<Plan>().AddAsync(plan, ct);

        // `manager` is also the plan creator (already authorized to activate via that path), but the
        // brief calls for an explicit PlanActivator row too — exercises the same authorization branch
        // a plan author who is NOT the creator would rely on.
        await uow.Repo<PlanActivator>().AddAsync(new PlanActivator { PlanId = plan.Id, UserId = manager.Id }, ct);

        var roadsTeam = new Team { PlanId = plan.Id, Name = "فريق الطرق", TeamLeaderUserId = leader.Id };
        var drainageTeam = new Team { PlanId = plan.Id, Name = "فريق الصرف الصحي" };
        await uow.Repo<Team>().AddRangeAsync([roadsTeam, drainageTeam], ct);

        await uow.Repo<TeamMembership>().AddRangeAsync(
        [
            new TeamMembership { TeamId = roadsTeam.Id, UserId = leader.Id },
            new TeamMembership { TeamId = roadsTeam.Id, UserId = member.Id },
            new TeamMembership { TeamId = drainageTeam.Id, UserId = substitute.Id },
        ], ct);

        await uow.Repo<TaskTemplate>().AddRangeAsync(
        [
            new TaskTemplate { TeamId = roadsTeam.Id, Title = "إغلاق الطرق المتأثرة", Order = 1, Duration = TimeSpan.FromMinutes(30) },
            new TaskTemplate { TeamId = roadsTeam.Id, Title = "نشر إشارات التحويلة", Order = 2, Duration = TimeSpan.FromHours(1) },
            new TaskTemplate { TeamId = drainageTeam.Id, Title = "فحص مضخات الصرف", Order = 1, Duration = TimeSpan.FromMinutes(45) },
        ], ct);

        // Roster for WHATEVER shift is current right now, so the plan activates cleanly on first boot
        // no matter the wall-clock time — `leader` and `member` on duty for the roads team, and
        // `substitute` frozen-in as the stand-in for `member` (FR-ESC-2/3's induction source).
        var shift = shiftCalc.Resolve(clock.UtcNow);
        await uow.Repo<ShiftAssignment>().AddRangeAsync(
        [
            new ShiftAssignment { TeamId = roadsTeam.Id, UserId = leader.Id, Shift = shift.Band, Date = shift.RosterDate },
            new ShiftAssignment { TeamId = roadsTeam.Id, UserId = member.Id, Shift = shift.Band, Date = shift.RosterDate },
            new ShiftAssignment
            {
                TeamId = roadsTeam.Id, UserId = substitute.Id, Shift = shift.Band, Date = shift.RosterDate,
                SubstituteForUserId = member.Id,
            },
        ], ct);

        await uow.SaveChangesAsync(ct);
    }
}
