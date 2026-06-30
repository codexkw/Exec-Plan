using ExecPlan.Application.Activation;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Notifications;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Realtime;
using ExecPlan.Application.Shifts;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class ActivationServiceTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public ActivationServiceTests(SqliteFixture fx) => _fx = fx;

    // A Kuwait Morning instant (09:00 local = 06:00 UTC) that rosters against 2026-06-30.
    private static readonly DateTime MorningUtc = KwtToUtc(2026, 6, 30, 9, 0);
    private static readonly DateTime RosterDate = new(2026, 6, 30);

    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    private sealed record Seed(
        Guid PlanId, Guid ManagerId, Guid Member1Id, Guid Member2Id, Guid SubstituteId,
        Guid TeamId, Guid OutsiderId, Guid ActivatorUserId, Guid AdminId);

    private ActivationService NewService()
    {
        var ctx = _fx.NewContext();
        var uow = new UnitOfWork(ctx);
        var clock = new TestClock { UtcNow = MorningUtc };
        return new ActivationService(
            uow, clock, new KuwaitShiftCalculator(),
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier(),
            new EscalationOptions { DefaultThreshold = 5 });
    }

    // Seeds an org, a manager (creator), two members + a substitute, an outsider, an activator and an
    // admin, plus a plan/team/two templates and a roster for `rosterShift` (substitute stands in for
    // member2). Set rosterShift to a band other than Morning to make the Morning resolution empty.
    private Seed SeedScenario(ShiftBand rosterShift = ShiftBand.Morning)
    {
        using var ctx = _fx.NewContext();

        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        ctx.Add(org);

        User MakeUser(UserRole role) => new()
        {
            UserName = $"u-{Guid.NewGuid():N}",
            FullName = "T",
            Phone = "+96500000000",
            PasswordHash = "x",
            Role = role,
            OrganizationId = org.Id,
            IsActive = true,
        };

        var manager = MakeUser(UserRole.PlanManager);
        var member1 = MakeUser(UserRole.TeamMember);
        var member2 = MakeUser(UserRole.TeamMember);
        var substitute = MakeUser(UserRole.TeamMember);
        var outsider = MakeUser(UserRole.TeamMember);
        var activator = MakeUser(UserRole.PlanManager);
        var admin = MakeUser(UserRole.SystemAdmin);
        ctx.AddRange(manager, member1, member2, substitute, outsider, activator, admin);

        var plan = new Plan
        {
            Name = "P", Type = PlanType.Guard, Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Add(plan);
        ctx.Add(new PlanActivator { PlanId = plan.Id, UserId = activator.Id });

        var team = new Team { PlanId = plan.Id, Name = "Alpha" };
        ctx.Add(team);

        ctx.Add(new TaskTemplate { TeamId = team.Id, Title = "Task A", Order = 1, Duration = TimeSpan.FromMinutes(30) });
        ctx.Add(new TaskTemplate { TeamId = team.Id, Title = "Task B", Order = 2, Duration = TimeSpan.FromHours(2) });

        ctx.Add(new ShiftAssignment { TeamId = team.Id, UserId = member1.Id, Shift = rosterShift, Date = RosterDate });
        ctx.Add(new ShiftAssignment { TeamId = team.Id, UserId = member2.Id, Shift = rosterShift, Date = RosterDate });
        ctx.Add(new ShiftAssignment
        {
            TeamId = team.Id, UserId = substitute.Id, Shift = rosterShift, Date = RosterDate,
            SubstituteForUserId = member2.Id,
        });

        ctx.SaveChanges();

        return new Seed(plan.Id, manager.Id, member1.Id, member2.Id, substitute.Id,
            team.Id, outsider.Id, activator.Id, admin.Id);
    }

    [Fact]
    public async Task Activate_creates_snapshot_participants_tasks_and_first_call()
    {
        var s = SeedScenario();

        var activationId = await NewService().ActivateAsync(s.PlanId, s.ManagerId);

        await using var ctx = _fx.NewContext();
        var activation = ctx.Set<PlanActivation>().Single(a => a.Id == activationId);
        activation.Status.Should().Be(ActivationStatus.Active);
        activation.Shift.Should().Be(ShiftBand.Morning);
        activation.RosterDate.Should().Be(RosterDate);
        activation.EscalationThreshold.Should().Be(5);
        activation.ActivatedByUserId.Should().Be(s.ManagerId);

        ctx.Set<PlanActivation>().Count(a => a.PlanId == s.PlanId && a.Status == ActivationStatus.Active)
            .Should().Be(1);

        var participants = ctx.Set<ActivationParticipant>().Where(p => p.ActivationId == activationId).ToList();
        participants.Should().HaveCount(2);
        participants.Should().OnlyContain(p => p.Status == ParticipantStatus.Pending);
        participants.Should().OnlyContain(p => p.CallAttemptCount == 1);
        participants.Should().OnlyContain(p => p.TeamNameSnapshot == "Alpha");

        // Each participant gets one task per template (2), with DueAt = ActivatedAt + Duration.
        foreach (var p in participants)
        {
            var tasks = ctx.Set<ExecutionTask>().Where(t => t.ParticipantId == p.Id).OrderBy(t => t.Order).ToList();
            tasks.Should().HaveCount(2);
            tasks[0].Title.Should().Be("Task A");
            tasks[0].DueAtUtc.Should().Be(activation.ActivatedAtUtc + TimeSpan.FromMinutes(30));
            tasks[1].Title.Should().Be("Task B");
            tasks[1].DueAtUtc.Should().Be(activation.ActivatedAtUtc + TimeSpan.FromHours(2));
        }

        ctx.Set<NotificationLog>().Count(n => n.ActivationId == activationId).Should().Be(2);
        var calls = ctx.Set<CallAttempt>().Where(c => c.ActivationId == activationId).ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(c => c.AttemptNumber == 1);
    }

    [Fact]
    public async Task Activate_freezes_substitute_from_roster()
    {
        var s = SeedScenario();

        var activationId = await NewService().ActivateAsync(s.PlanId, s.ManagerId);

        await using var ctx = _fx.NewContext();
        var participants = ctx.Set<ActivationParticipant>().Where(p => p.ActivationId == activationId).ToList();

        var member2Participant = participants.Single(p => p.UserId == s.Member2Id);
        member2Participant.ResolvedSubstituteUserId.Should().Be(s.SubstituteId);

        var member1Participant = participants.Single(p => p.UserId == s.Member1Id);
        member1Participant.ResolvedSubstituteUserId.Should().BeNull();
    }

    [Fact]
    public async Task Activate_rejects_when_already_active()
    {
        var s = SeedScenario();
        await NewService().ActivateAsync(s.PlanId, s.ManagerId);

        var act = async () => await NewService().ActivateAsync(s.PlanId, s.ManagerId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Conflict);
    }

    [Fact]
    public async Task Activate_rejects_when_no_one_on_duty()
    {
        // Roster exists only for Evening, so the Morning resolution finds nobody.
        var s = SeedScenario(rosterShift: ShiftBand.Evening);

        var act = async () => await NewService().ActivateAsync(s.PlanId, s.ManagerId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Conflict);
    }

    [Fact]
    public async Task Activate_rejects_unauthorized_user()
    {
        var s = SeedScenario();

        var act = async () => await NewService().ActivateAsync(s.PlanId, s.OutsiderId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Activate_allows_registered_plan_activator()
    {
        var s = SeedScenario();

        var activationId = await NewService().ActivateAsync(s.PlanId, s.ActivatorUserId);

        await using var ctx = _fx.NewContext();
        ctx.Set<PlanActivation>().Single(a => a.Id == activationId).ActivatedByUserId
            .Should().Be(s.ActivatorUserId);
    }

    [Fact]
    public async Task Activate_allows_system_admin()
    {
        var s = SeedScenario();

        var activationId = await NewService().ActivateAsync(s.PlanId, s.AdminId);

        await using var ctx = _fx.NewContext();
        ctx.Set<PlanActivation>().Single(a => a.Id == activationId).ActivatedByUserId
            .Should().Be(s.AdminId);
    }

    [Fact]
    public async Task Activate_rejects_missing_plan()
    {
        var act = async () => await NewService().ActivateAsync(Guid.NewGuid(), Guid.NewGuid());

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.NotFound);
    }
}
