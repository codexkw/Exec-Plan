using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Broadcast;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
using ExecPlan.Application.Execution;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Notifications;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Realtime;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class ExecutionAndBroadcastTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public ExecutionAndBroadcastTests(SqliteFixture fx) => _fx = fx;

    private static readonly DateTime MorningUtc = KwtToUtc(2026, 6, 30, 9, 0);
    private static readonly DateTime RosterDate = new(2026, 6, 30);

    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    // Each service gets its OWN context/UoW (mirrors request scoping). The provider shares the service's
    // UoW so its staged rows commit with the service's single SaveChanges; the DashboardService injected
    // into ExecutionService.CloseAsync uses a separate context (it only reads committed data).
    private ExecutionService NewExecution(ICurrentUser cur)
    {
        var uow = new UnitOfWork(_fx.NewContext());
        var dashboard = new DashboardService(new UnitOfWork(_fx.NewContext()), new TestClock { UtcNow = MorningUtc });
        return new ExecutionService(
            uow, new TestClock { UtcNow = MorningUtc }, cur,
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier(), dashboard);
    }

    private BroadcastService NewBroadcast(ICurrentUser cur)
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new BroadcastService(
            uow, new TestClock { UtcNow = MorningUtc }, cur,
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier());
    }

    private sealed record Seed(
        Guid ActivationId,
        Guid OtherActivationId,
        Guid PlanCreatorId,
        Guid ManagerId,
        Guid LeaderAlphaId,
        Guid LeaderBetaId,
        Guid MemberA1Id,
        Guid MemberA2Id,
        Guid MemberB1Id,
        Guid ParticipantA1Id,
        Guid ParticipantA2Id,
        Guid ParticipantB1Id,
        Guid TaskA1Id,
        Guid OtherParticipantId);

    // One Active activation with TWO teams under one plan, each team with a DISTINCT leader, plus a
    // second activation under another plan — enough to exercise owner/leader/manager authorization and
    // the cross-team + cross-activation reassign boundaries.
    private Seed SeedScenario()
    {
        using var ctx = _fx.NewContext();

        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        ctx.Add(org);

        User MakeUser(UserRole role) => new()
        {
            UserName = $"u-{Guid.NewGuid():N}", FullName = "T", Phone = "+96500000000",
            PasswordHash = "x", Role = role, OrganizationId = org.Id, IsActive = true,
        };

        var manager = MakeUser(UserRole.PlanManager);
        var leaderAlpha = MakeUser(UserRole.TeamLeader);
        var leaderBeta = MakeUser(UserRole.TeamLeader);
        var memberA1 = MakeUser(UserRole.TeamMember);
        var memberA2 = MakeUser(UserRole.TeamMember);
        var memberB1 = MakeUser(UserRole.TeamMember);
        ctx.AddRange(manager, leaderAlpha, leaderBeta, memberA1, memberA2, memberB1);

        var plan = new Plan { Name = "P", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
        ctx.Add(plan);

        var teamAlpha = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leaderAlpha.Id };
        var teamBeta = new Team { PlanId = plan.Id, Name = "Beta", TeamLeaderUserId = leaderBeta.Id };
        ctx.AddRange(teamAlpha, teamBeta);

        var activation = new PlanActivation
        {
            PlanId = plan.Id, Status = ActivationStatus.Active, Shift = ShiftBand.Morning,
            RosterDate = RosterDate, ActivatedByUserId = manager.Id, ActivatedAtUtc = MorningUtc,
            EscalationThreshold = 5,
        };
        ctx.Add(activation);

        ActivationParticipant MakeParticipant(Guid userId, Team team) => new()
        {
            ActivationId = activation.Id, UserId = userId, TeamId = team.Id,
            TeamNameSnapshot = team.Name, Status = ParticipantStatus.Pending,
        };

        var pA1 = MakeParticipant(memberA1.Id, teamAlpha);
        var pA2 = MakeParticipant(memberA2.Id, teamAlpha);
        var pB1 = MakeParticipant(memberB1.Id, teamBeta);
        ctx.AddRange(pA1, pA2, pB1);

        var taskA1 = new ExecutionTask
        {
            ActivationId = activation.Id, ParticipantId = pA1.Id, Title = "Task A", Order = 1,
            Status = ExecTaskStatus.Pending, DueAtUtc = MorningUtc.AddHours(1),
        };
        ctx.Add(taskA1);

        // A second activation under another plan, with one participant — the cross-activation target.
        var otherPlan = new Plan { Name = "P2", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
        ctx.Add(otherPlan);
        var otherActivation = new PlanActivation
        {
            PlanId = otherPlan.Id, Status = ActivationStatus.Active, Shift = ShiftBand.Morning,
            RosterDate = RosterDate, ActivatedByUserId = manager.Id, ActivatedAtUtc = MorningUtc,
            EscalationThreshold = 5,
        };
        ctx.Add(otherActivation);
        var otherTeam = new Team { PlanId = otherPlan.Id, Name = "Gamma", TeamLeaderUserId = leaderAlpha.Id };
        ctx.Add(otherTeam);
        var otherParticipant = new ActivationParticipant
        {
            ActivationId = otherActivation.Id, UserId = memberB1.Id, TeamId = otherTeam.Id,
            TeamNameSnapshot = "Gamma", Status = ParticipantStatus.Pending,
        };
        ctx.Add(otherParticipant);

        ctx.SaveChanges();

        return new Seed(
            activation.Id, otherActivation.Id, manager.Id, manager.Id, leaderAlpha.Id, leaderBeta.Id,
            memberA1.Id, memberA2.Id, memberB1.Id, pA1.Id, pA2.Id, pB1.Id, taskA1.Id, otherParticipant.Id);
    }

    [Fact]
    public async Task Member_marks_own_task_done_with_note()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.MemberA1Id, Role = UserRole.TeamMember };

        await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: true, note: "secured", reassignToParticipantId: null);

        await using var ctx = _fx.NewContext();
        var task = ctx.Set<ExecutionTask>().Single(t => t.Id == s.TaskA1Id);
        task.Status.Should().Be(ExecTaskStatus.Done);
        task.Note.Should().Be("secured");
        task.CompletedAtUtc.Should().Be(MorningUtc);
    }

    [Fact]
    public async Task Member_cannot_update_another_members_task()
    {
        var s = SeedScenario();
        // memberA2 is in the same team but does NOT own taskA1 and is not a leader/manager.
        var cur = new FakeCurrentUser { UserId = s.MemberA2Id, Role = UserRole.TeamMember };

        var act = async () =>
            await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: true, note: null, reassignToParticipantId: null);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Leader_reassign_within_led_team_succeeds()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };

        // Reassign taskA1 (Alpha) from pA1 to pA2 — both in Alpha, which leaderAlpha leads.
        await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: null, note: null, reassignToParticipantId: s.ParticipantA2Id);

        await using var ctx = _fx.NewContext();
        var task = ctx.Set<ExecutionTask>().Single(t => t.Id == s.TaskA1Id);
        task.ParticipantId.Should().Be(s.ParticipantA2Id);
    }

    [Fact]
    public async Task Leader_reassign_across_team_boundary_is_Forbidden()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };

        // leaderAlpha leads Alpha (source) but NOT Beta (target) → cross-boundary reassign forbidden.
        var act = async () =>
            await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: null, note: null, reassignToParticipantId: s.ParticipantB1Id);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Reassign_to_participant_of_other_activation_is_rejected()
    {
        var s = SeedScenario();
        // Manager is fully authorized, isolating the cross-activation Validation rule.
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };

        var act = async () =>
            await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: null, note: null, reassignToParticipantId: s.OtherParticipantId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Validation);
    }

    [Fact]
    public async Task Set_substitute_live_updates_resolved_substitute()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };
        var substituteUserId = Guid.NewGuid();

        await NewExecution(cur).SetSubstituteLiveAsync(s.ActivationId, s.ParticipantA1Id, substituteUserId);

        await using var ctx = _fx.NewContext();
        var p = ctx.Set<ActivationParticipant>().Single(x => x.Id == s.ParticipantA1Id);
        p.ResolvedSubstituteUserId.Should().Be(substituteUserId);
    }

    [Fact]
    public async Task Raise_issue_creates_notification_to_plan_creator()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };

        await NewExecution(cur).RaiseIssueAsync(s.ActivationId, "radio is down");

        await using var ctx = _fx.NewContext();
        var notifications = ctx.Set<NotificationLog>()
            .Where(n => n.ActivationId == s.ActivationId && n.Kind == NotificationKind.Notification)
            .ToList();
        notifications.Should().ContainSingle();
        notifications[0].RecipientUserId.Should().Be(s.PlanCreatorId);
        notifications[0].Body.Should().StartWith("Issue:");
    }

    [Fact]
    public async Task Broadcast_creates_message_and_one_notification_per_participant()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };

        await NewBroadcast(cur).BroadcastAsync(s.ActivationId, "move out");

        await using var ctx = _fx.NewContext();

        var messages = ctx.Set<BroadcastMessage>().Where(b => b.ActivationId == s.ActivationId).ToList();
        messages.Should().ContainSingle();
        messages[0].SenderUserId.Should().Be(s.ManagerId);
        messages[0].Body.Should().Be("move out");

        // One Broadcast-kind notification per participant of THIS activation (pA1, pA2, pB1 = 3).
        ctx.Set<NotificationLog>()
            .Count(n => n.ActivationId == s.ActivationId && n.Kind == NotificationKind.Broadcast)
            .Should().Be(3);
    }

    [Fact]
    public async Task Close_sets_status_closed_and_returns_dashboard()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };

        var snapshot = await NewExecution(cur).CloseAsync(s.ActivationId);

        snapshot.Should().BeOfType<DashboardDto>();
        snapshot.ActivationId.Should().Be(s.ActivationId);
        snapshot.Status.Should().Be(ActivationStatus.Closed);
        snapshot.TotalParticipants.Should().Be(3);

        await using var ctx = _fx.NewContext();
        var activation = ctx.Set<PlanActivation>().Single(a => a.Id == s.ActivationId);
        activation.Status.Should().Be(ActivationStatus.Closed);
        activation.ClosedAtUtc.Should().Be(MorningUtc);
    }

    [Fact]
    public async Task Closing_an_already_closed_activation_throws_Conflict()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };

        await NewExecution(cur).CloseAsync(s.ActivationId);

        var act = async () => await NewExecution(cur).CloseAsync(s.ActivationId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Conflict);
    }

    // --- Negative-authorization gate tests (pin each gate so a future refactor can't silently widen it) ---

    [Fact]
    public async Task Member_reassigning_a_task_is_Forbidden()
    {
        var s = SeedScenario();
        // memberA1 owns taskA1 (so done/note-only would be allowed) but is a plain member — not a
        // leader/manager/admin — so any reassign attempt, even within the same team, must be Forbidden.
        var cur = new FakeCurrentUser { UserId = s.MemberA1Id, Role = UserRole.TeamMember };

        var act = async () =>
            await NewExecution(cur).UpdateTaskAsync(s.TaskA1Id, done: null, note: null, reassignToParticipantId: s.ParticipantA2Id);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task SetSubstituteLive_by_non_leading_leader_is_Forbidden()
    {
        var s = SeedScenario();
        // leaderBeta leads Beta, not Alpha — pA1 belongs to Alpha, so leaderBeta must be Forbidden.
        var cur = new FakeCurrentUser { UserId = s.LeaderBetaId, Role = UserRole.TeamLeader };
        var substituteUserId = Guid.NewGuid();

        var act = async () =>
            await NewExecution(cur).SetSubstituteLiveAsync(s.ActivationId, s.ParticipantA1Id, substituteUserId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task RaiseIssue_by_member_is_Forbidden()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.MemberA1Id, Role = UserRole.TeamMember };

        var act = async () => await NewExecution(cur).RaiseIssueAsync(s.ActivationId, "radio is down");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Broadcast_by_member_is_Forbidden()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.MemberA1Id, Role = UserRole.TeamMember };

        var act = async () => await NewBroadcast(cur).BroadcastAsync(s.ActivationId, "move out");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Broadcast_by_leader_is_Forbidden()
    {
        var s = SeedScenario();
        // A team leader has no broadcast authority — only manager/admin does.
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };

        var act = async () => await NewBroadcast(cur).BroadcastAsync(s.ActivationId, "move out");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Close_by_member_is_Forbidden()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.MemberA1Id, Role = UserRole.TeamMember };

        var act = async () => await NewExecution(cur).CloseAsync(s.ActivationId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    [Fact]
    public async Task Close_by_leader_is_Forbidden()
    {
        var s = SeedScenario();
        // A team leader has no close authority — only manager/admin does.
        var cur = new FakeCurrentUser { UserId = s.LeaderAlphaId, Role = UserRole.TeamLeader };

        var act = async () => await NewExecution(cur).CloseAsync(s.ActivationId);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }

    // --- BroadcastService robustness: unknown activation must not orphan a BroadcastMessage ---

    [Fact]
    public async Task Broadcast_to_unknown_activation_throws_NotFound()
    {
        var s = SeedScenario();
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };

        var act = async () => await NewBroadcast(cur).BroadcastAsync(Guid.NewGuid(), "move out");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.NotFound);
    }
}
