using ExecPlan.Application.Activation;
using ExecPlan.Application.Common;
using ExecPlan.Application.Escalation;
using ExecPlan.Application.Execution;
using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Notifications;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Realtime;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class EscalationServiceTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public EscalationServiceTests(SqliteFixture fx) => _fx = fx;

    // A Kuwait Morning instant (09:00 local = 06:00 UTC) that rosters against 2026-06-30.
    private static readonly DateTime MorningUtc = KwtToUtc(2026, 6, 30, 9, 0);
    private static readonly DateTime RosterDate = new(2026, 6, 30);

    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    private sealed record Seed(
        Guid PlanId, Guid ManagerId, Guid Member1Id, Guid Member2Id, Guid SubstituteId, Guid TeamId);

    // Each service gets its OWN context/UoW, mirroring request scoping and avoiding stale tracking
    // across the activate → acknowledge → escalate sequence.
    private ActivationService NewActivationService(int threshold)
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new ActivationService(
            uow, new TestClock { UtcNow = MorningUtc }, new KuwaitShiftCalculator(),
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier(),
            new EscalationOptions { DefaultThreshold = threshold });
    }

    private AcknowledgeService NewAcknowledgeService()
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new AcknowledgeService(uow, new TestClock { UtcNow = MorningUtc }, new NoOpRealtimeNotifier());
    }

    private EscalationService NewEscalationService()
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new EscalationService(
            uow, new TestClock { UtcNow = MorningUtc },
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier());
    }

    // Seeds a plan/team/two-templates and a Morning roster where `substitute` stands in for `member2`.
    private Seed SeedScenario()
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
        ctx.AddRange(manager, member1, member2, substitute);

        var plan = new Plan
        {
            Name = "P", Type = PlanType.Guard, Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Add(plan);

        var team = new Team { PlanId = plan.Id, Name = "Alpha" };
        ctx.Add(team);

        ctx.Add(new TaskTemplate { TeamId = team.Id, Title = "Task A", Order = 1, Duration = TimeSpan.FromMinutes(30) });
        ctx.Add(new TaskTemplate { TeamId = team.Id, Title = "Task B", Order = 2, Duration = TimeSpan.FromHours(2) });

        ctx.Add(new ShiftAssignment { TeamId = team.Id, UserId = member1.Id, Shift = ShiftBand.Morning, Date = RosterDate });
        ctx.Add(new ShiftAssignment { TeamId = team.Id, UserId = member2.Id, Shift = ShiftBand.Morning, Date = RosterDate });
        ctx.Add(new ShiftAssignment
        {
            TeamId = team.Id, UserId = substitute.Id, Shift = ShiftBand.Morning, Date = RosterDate,
            SubstituteForUserId = member2.Id,
        });

        ctx.SaveChanges();

        return new Seed(plan.Id, manager.Id, member1.Id, member2.Id, substitute.Id, team.Id);
    }

    [Fact]
    public async Task At_threshold_non_responder_is_escalated_and_substitute_inducted()
    {
        var s = SeedScenario();
        // threshold=2; activation seeds each participant at CallAttemptCount=1.
        var activationId = await NewActivationService(threshold: 2).ActivateAsync(s.PlanId, s.ManagerId);

        // member1 confirms readiness → Ready; only member2 stays Pending.
        await NewAcknowledgeService().AcknowledgeAsync(activationId, s.Member1Id);

        var result = await NewEscalationService().RunCycleAsync(activationId);

        // Only the one pending participant got an attempt; that attempt tipped it over → 1 induction.
        result.AttemptsAdded.Should().Be(1);
        result.Inducted.Should().Be(1);

        await using var ctx = _fx.NewContext();

        var member1Participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == s.Member1Id);
        member1Participant.Status.Should().Be(ParticipantStatus.Ready);
        member1Participant.CallAttemptCount.Should().Be(1); // untouched — Ready, not Pending.

        var member2Participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == s.Member2Id);
        member2Participant.Status.Should().Be(ParticipantStatus.Escalated);
        member2Participant.CallAttemptCount.Should().Be(2); // 1 → 2, reached threshold.

        var sub = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.IsSubstitute);
        sub.UserId.Should().Be(s.SubstituteId);
        sub.InductedFromParticipantId.Should().Be(member2Participant.Id);
        sub.Status.Should().Be(ParticipantStatus.Pending);
        sub.TeamId.Should().Be(s.TeamId);
        sub.TeamNameSnapshot.Should().Be("Alpha");
        sub.ResolvedSubstituteUserId.Should().BeNull();
        sub.CallAttemptCount.Should().Be(1);

        // Full task set generated fresh from the team templates.
        var subTasks = ctx.Set<ExecutionTask>().Where(t => t.ParticipantId == sub.Id).OrderBy(t => t.Order).ToList();
        subTasks.Should().HaveCount(2);
        subTasks[0].Title.Should().Be("Task A");
        subTasks[0].DueAtUtc.Should().Be(MorningUtc + TimeSpan.FromMinutes(30));
        subTasks[1].Title.Should().Be("Task B");
        subTasks[1].DueAtUtc.Should().Be(MorningUtc + TimeSpan.FromHours(2));

        // One notification to the substitute, and their first call attempt.
        ctx.Set<NotificationLog>().Count(n => n.ActivationId == activationId && n.RecipientUserId == s.SubstituteId)
            .Should().Be(1);
        ctx.Set<CallAttempt>().Count(c => c.ActivationId == activationId && c.ParticipantId == sub.Id && c.AttemptNumber == 1)
            .Should().Be(1);

        // member2 got a second call attempt this cycle (number 2).
        ctx.Set<CallAttempt>().Count(c => c.ActivationId == activationId && c.ParticipantId == member2Participant.Id)
            .Should().Be(2);

        // The escalation log links the escalated participant to the inducted substitute.
        var log = ctx.Set<EscalationLog>().Single(e => e.ActivationId == activationId);
        log.ParticipantId.Should().Be(member2Participant.Id);
        log.SubstituteUserId.Should().Be(s.SubstituteId);
        log.NewParticipantId.Should().Be(sub.Id);
    }

    [Fact]
    public async Task Cycle_below_threshold_adds_one_attempt_per_pending_participant_and_inducts_nobody()
    {
        var s = SeedScenario();
        // threshold=5; activation seeds each participant at CallAttemptCount=1, so one cycle stays below.
        var activationId = await NewActivationService(threshold: 5).ActivateAsync(s.PlanId, s.ManagerId);

        var result = await NewEscalationService().RunCycleAsync(activationId);

        result.AttemptsAdded.Should().Be(2); // both members still Pending.
        result.Inducted.Should().Be(0);

        await using var ctx = _fx.NewContext();

        var participants = ctx.Set<ActivationParticipant>().Where(p => p.ActivationId == activationId).ToList();
        participants.Should().HaveCount(2); // no substitute inducted.
        participants.Should().OnlyContain(p => p.Status == ParticipantStatus.Pending);
        participants.Should().OnlyContain(p => p.CallAttemptCount == 2); // 1 → 2, still below 5.

        ctx.Set<ActivationParticipant>().Count(p => p.ActivationId == activationId && p.IsSubstitute).Should().Be(0);
        ctx.Set<EscalationLog>().Count(e => e.ActivationId == activationId).Should().Be(0);
    }

    [Fact]
    public async Task Ready_participant_is_not_escalated()
    {
        var s = SeedScenario();
        var activationId = await NewActivationService(threshold: 2).ActivateAsync(s.PlanId, s.ManagerId);

        await NewAcknowledgeService().AcknowledgeAsync(activationId, s.Member1Id);

        // Run enough cycles that, had member1 been Pending, it would have escalated.
        await NewEscalationService().RunCycleAsync(activationId);
        await NewEscalationService().RunCycleAsync(activationId);

        await using var ctx = _fx.NewContext();
        var member1Participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == s.Member1Id);

        member1Participant.Status.Should().Be(ParticipantStatus.Ready);
        member1Participant.CallAttemptCount.Should().Be(1); // never received another attempt.
        ctx.Set<EscalationLog>().Count(e => e.ParticipantId == member1Participant.Id).Should().Be(0);
    }

    [Fact]
    public async Task Escalating_a_closed_activation_throws_Conflict()
    {
        var s = SeedScenario();
        var activationId = await NewActivationService(threshold: 2).ActivateAsync(s.PlanId, s.ManagerId);

        // Arrange a closed activation directly.
        using (var ctx = _fx.NewContext())
        {
            var activation = ctx.Set<PlanActivation>().Single(a => a.Id == activationId);
            activation.Status = ActivationStatus.Closed;
            ctx.SaveChanges();
        }

        var act = async () => await NewEscalationService().RunCycleAsync(activationId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Conflict);
    }

    [Fact]
    public async Task Escalating_an_unknown_activation_throws_NotFound()
    {
        var act = async () => await NewEscalationService().RunCycleAsync(Guid.NewGuid());

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.NotFound);
    }
}
