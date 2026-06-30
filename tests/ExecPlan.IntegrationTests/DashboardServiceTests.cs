using ExecPlan.Application.Activation;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
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

public class DashboardServiceTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public DashboardServiceTests(SqliteFixture fx) => _fx = fx;

    // A Kuwait Morning instant (09:00 local = 06:00 UTC) that rosters against 2026-06-30 (for the
    // activation-flow tests, mirroring EscalationServiceTests).
    private static readonly DateTime MorningUtc = KwtToUtc(2026, 6, 30, 9, 0);
    private static readonly DateTime RosterDate = new(2026, 6, 30);

    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    // Each service gets its OWN context/UoW, mirroring request scoping.
    private DashboardService NewDashboard(DateTime now) =>
        new(new UnitOfWork(_fx.NewContext()), new TestClock { UtcNow = now });

    // Service + provider MUST share the one UoW/context so the provider's staged rows are committed by
    // the service's single SaveChanges (mirrors EscalationServiceTests).
    private ActivationService NewActivationService(int threshold)
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new ActivationService(
            uow, new TestClock { UtcNow = MorningUtc }, new KuwaitShiftCalculator(),
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier(),
            new EscalationOptions { DefaultThreshold = threshold });
    }

    private AcknowledgeService NewAcknowledgeService() =>
        new(new UnitOfWork(_fx.NewContext()), new TestClock { UtcNow = MorningUtc }, new NoOpRealtimeNotifier());

    private EscalationService NewEscalationService()
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new EscalationService(
            uow, new TestClock { UtcNow = MorningUtc },
            new DatabasePlaceholderProvider(uow), new NoOpRealtimeNotifier());
    }

    private sealed record Seed(Guid PlanId, Guid ManagerId, Guid Member1Id, Guid Member2Id, Guid TeamId);

    // Plan/team/two-templates + a Morning roster where `substitute` stands in for `member2`.
    private Seed SeedScenario()
    {
        using var ctx = _fx.NewContext();

        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        ctx.Add(org);

        User MakeUser() => new()
        {
            UserName = $"u-{Guid.NewGuid():N}", FullName = "T", Phone = "+96500000000",
            PasswordHash = "x", Role = UserRole.TeamMember, OrganizationId = org.Id, IsActive = true,
        };

        var manager = MakeUser();
        manager.Role = UserRole.PlanManager;
        var member1 = MakeUser();
        var member2 = MakeUser();
        var substitute = MakeUser();
        ctx.AddRange(manager, member1, member2, substitute);

        var plan = new Plan { Name = "P", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
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
        return new Seed(plan.Id, manager.Id, member1.Id, member2.Id, team.Id);
    }

    [Fact]
    public async Task Snapshot_counts_participants_by_status()
    {
        var s = SeedScenario();
        var activationId = await NewActivationService(threshold: 2).ActivateAsync(s.PlanId, s.ManagerId);

        // After activation: both members Pending.
        var afterActivate = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        afterActivate.TotalParticipants.Should().Be(2);
        afterActivate.PendingCount.Should().Be(2);
        afterActivate.ReadyCount.Should().Be(0);
        afterActivate.EscalatedCount.Should().Be(0);
        afterActivate.InductedCount.Should().Be(0);

        // After one acknowledge: Pending → Ready shift.
        await NewAcknowledgeService().AcknowledgeAsync(activationId, s.Member1Id);
        var afterAck = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        afterAck.PendingCount.Should().Be(1);
        afterAck.ReadyCount.Should().Be(1);

        // After escalation to threshold: member2 Escalated + a substitute Inducted appear (DEC-16).
        await NewEscalationService().RunCycleAsync(activationId);
        var afterEsc = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        afterEsc.TotalParticipants.Should().Be(3);
        afterEsc.PendingCount.Should().Be(0);
        afterEsc.ReadyCount.Should().Be(1);
        afterEsc.EscalatedCount.Should().Be(1);
        afterEsc.InductedCount.Should().Be(1);

        // Mutually exclusive: Total == sum of the four counters.
        (afterEsc.PendingCount + afterEsc.ReadyCount + afterEsc.EscalatedCount + afterEsc.InductedCount)
            .Should().Be(afterEsc.TotalParticipants);

        // The synthesized feed unions multiple sources from the realistic flow.
        afterEsc.Events.Select(e => e.Type).Distinct()
            .Should().Contain(new[] { "notification", "call", "response", "escalation" });
    }

    [Fact]
    public async Task Snapshot_reflects_activation_status_shift_and_roster_date()
    {
        var s = SeedScenario();
        var activationId = await NewActivationService(threshold: 5).ActivateAsync(s.PlanId, s.ManagerId);

        // Freshly activated: Active, Morning, the seeded roster date (DEC-17 contract restoration).
        var active = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        active.Status.Should().Be(ActivationStatus.Active);
        active.Shift.Should().Be(ShiftBand.Morning);
        active.RosterDate.Should().Be(RosterDate);

        // After CloseAsync, a fresh GetSnapshotAsync reflects the activation's new Status.
        var cur = new FakeCurrentUser { UserId = s.ManagerId, Role = UserRole.PlanManager };
        var executionUow = new UnitOfWork(_fx.NewContext());
        var execution = new ExecutionService(
            executionUow, new TestClock { UtcNow = MorningUtc }, cur,
            new DatabasePlaceholderProvider(executionUow), new NoOpRealtimeNotifier(),
            NewDashboard(MorningUtc));

        var closedSnapshot = await execution.CloseAsync(activationId);
        closedSnapshot.Status.Should().Be(ActivationStatus.Closed);

        var freshAfterClose = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        freshAfterClose.Status.Should().Be(ActivationStatus.Closed);
    }

    [Fact]
    public async Task Response_and_completion_rates_are_correct()
    {
        var s = SeedScenario();
        var activationId = await NewActivationService(threshold: 5).ActivateAsync(s.PlanId, s.ManagerId);

        // After activation: nobody ready, no tasks done. 2 participants, 4 tasks (2 each).
        var initial = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        initial.ResponseRate.Should().Be(0d);
        initial.TaskCompletionRate.Should().Be(0d);

        // One acknowledge → 1 of 2 ready.
        await NewAcknowledgeService().AcknowledgeAsync(activationId, s.Member1Id);
        var afterAck = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        afterAck.ResponseRate.Should().Be(0.5d); // 1/2

        // Mark exactly one of the 4 tasks Done via the repo.
        using (var ctx = _fx.NewContext())
        {
            var task = ctx.Set<ExecutionTask>().First(t => t.ActivationId == activationId);
            task.Status = ExecTaskStatus.Done;
            task.CompletedAtUtc = MorningUtc;
            ctx.SaveChanges();
        }

        var afterDone = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);
        afterDone.TaskCompletionRate.Should().Be(0.25d); // 1/4
    }

    [Fact]
    public async Task Teams_sorted_best_to_delayed()
    {
        // Direct seed: two teams with different ready/done ratios under one activation.
        Guid activationId;
        var alphaTeamId = Guid.NewGuid();
        var betaTeamId = Guid.NewGuid();

        using (var ctx = _fx.NewContext())
        {
            var activation = new PlanActivation
            {
                PlanId = Guid.NewGuid(), Status = ActivationStatus.Active, Shift = ShiftBand.Morning,
                RosterDate = RosterDate, ActivatedByUserId = Guid.NewGuid(), ActivatedAtUtc = MorningUtc,
                EscalationThreshold = 5,
            };
            ctx.Add(activation);
            activationId = activation.Id;

            // Alpha: readyRatio 2/2 = 1.0, doneRatio 1/2 = 0.5 → score 0.75 (best).
            var a1 = AddParticipant(ctx, activationId, alphaTeamId, "Alpha", ParticipantStatus.Ready);
            var a2 = AddParticipant(ctx, activationId, alphaTeamId, "Alpha", ParticipantStatus.Ready);
            AddTask(ctx, activationId, a1.Id, "A1", ExecTaskStatus.Done);
            AddTask(ctx, activationId, a2.Id, "A2", ExecTaskStatus.Pending);

            // Beta: readyRatio 1/2 = 0.5, doneRatio 0/2 = 0 → score 0.25 (delayed).
            var b1 = AddParticipant(ctx, activationId, betaTeamId, "Beta", ParticipantStatus.Ready);
            var b2 = AddParticipant(ctx, activationId, betaTeamId, "Beta", ParticipantStatus.Pending);
            AddTask(ctx, activationId, b1.Id, "B1", ExecTaskStatus.Pending);
            AddTask(ctx, activationId, b2.Id, "B2", ExecTaskStatus.Pending);

            ctx.SaveChanges();
        }

        var snapshot = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);

        snapshot.Teams.Should().HaveCount(2);
        snapshot.Teams[0].TeamName.Should().Be("Alpha"); // best first
        snapshot.Teams[0].Score.Should().Be(0.75d);
        snapshot.Teams[1].TeamName.Should().Be("Beta"); // delayed last
        snapshot.Teams[1].Score.Should().Be(0.25d);
        snapshot.Teams.Should().BeInDescendingOrder(t => t.Score);
    }

    [Fact]
    public async Task Overdue_lists_pending_tasks_past_due()
    {
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        Guid activationId;
        Guid participantUserId;
        Guid overdueTaskId;

        using (var ctx = _fx.NewContext())
        {
            var activation = new PlanActivation
            {
                PlanId = Guid.NewGuid(), Status = ActivationStatus.Active, Shift = ShiftBand.Morning,
                RosterDate = RosterDate, ActivatedByUserId = Guid.NewGuid(), ActivatedAtUtc = now,
                EscalationThreshold = 5,
            };
            ctx.Add(activation);
            activationId = activation.Id;

            var p = AddParticipant(ctx, activationId, Guid.NewGuid(), "Alpha", ParticipantStatus.Pending);
            participantUserId = p.UserId;

            // Past-due AND Pending → overdue.
            var overdueTask = MakeTask(activationId, p.Id, "Late", ExecTaskStatus.Pending);
            overdueTask.DueAtUtc = now.AddHours(-1);
            ctx.Add(overdueTask);
            overdueTaskId = overdueTask.Id;

            // Future due, Pending → not overdue.
            var futureTask = MakeTask(activationId, p.Id, "Future", ExecTaskStatus.Pending);
            futureTask.DueAtUtc = now.AddHours(1);
            ctx.Add(futureTask);

            // Past-due but already Done → not overdue.
            var doneTask = MakeTask(activationId, p.Id, "DonePast", ExecTaskStatus.Done);
            doneTask.DueAtUtc = now.AddHours(-2);
            doneTask.CompletedAtUtc = now.AddHours(-1);
            ctx.Add(doneTask);

            ctx.SaveChanges();
        }

        var snapshot = await NewDashboard(now).GetSnapshotAsync(activationId);

        snapshot.Overdue.Should().HaveCount(1);
        snapshot.Overdue[0].TaskId.Should().Be(overdueTaskId);
        snapshot.Overdue[0].Title.Should().Be("Late");
        snapshot.Overdue[0].ParticipantUserId.Should().Be(participantUserId);
        snapshot.Overdue[0].DueAtUtc.Should().Be(now.AddHours(-1));
    }

    [Fact]
    public async Task Events_capped_at_50_and_newest_first()
    {
        var activationId = Guid.NewGuid();
        Guid broadcastId;

        using (var ctx = _fx.NewContext())
        {
            var activation = new PlanActivation
            {
                PlanId = Guid.NewGuid(), Status = ActivationStatus.Active, Shift = ShiftBand.Morning,
                RosterDate = RosterDate, ActivatedByUserId = Guid.NewGuid(), ActivatedAtUtc = MorningUtc,
                EscalationThreshold = 5,
            };
            ctx.Add(activation);
            activationId = activation.Id;

            var p = AddParticipant(ctx, activationId, Guid.NewGuid(), "Alpha", ParticipantStatus.Ready);

            // 60 completed tasks with controlled, strictly-ascending CompletedAtUtc in the far future,
            // so they dominate the (wall-clock-stamped) broadcast unless we override the broadcast time.
            var futureBase = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 60; i++)
            {
                var t = MakeTask(activationId, p.Id, $"T{i}", ExecTaskStatus.Done);
                t.DueAtUtc = futureBase;
                t.CompletedAtUtc = futureBase.AddMinutes(i); // T59 newest
                ctx.Add(t);
            }

            var broadcast = new BroadcastMessage { ActivationId = activationId, SenderUserId = Guid.NewGuid(), Body = "Move out" };
            ctx.Add(broadcast);
            broadcastId = broadcast.Id;

            ctx.SaveChanges();
        }

        // Override the broadcast's CreatedAtUtc to be the newest event of all (a tracked UPDATE only
        // stamps UpdatedAtUtc, leaving our explicit CreatedAtUtc intact).
        using (var ctx = _fx.NewContext())
        {
            var broadcast = ctx.Set<BroadcastMessage>().Single(b => b.Id == broadcastId);
            broadcast.CreatedAtUtc = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ctx.SaveChanges();
        }

        var snapshot = await NewDashboard(MorningUtc).GetSnapshotAsync(activationId);

        // 61 events total → capped at 50, newest first.
        snapshot.Events.Should().HaveCount(50);
        snapshot.Events.Should().BeInDescendingOrder(e => e.AtUtc);
        snapshot.Events[0].Type.Should().Be("broadcast"); // 2031, the absolute newest
        snapshot.Events[0].Text.Should().Be("Move out");
        snapshot.Events[1].Type.Should().Be("task");
        snapshot.Events[1].Text.Should().Be("T59 done"); // newest task
    }

    [Fact]
    public async Task Unknown_activation_throws_NotFound()
    {
        var act = async () => await NewDashboard(MorningUtc).GetSnapshotAsync(Guid.NewGuid());

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.NotFound);
    }

    // ---- direct-seed helpers ----

    private static ActivationParticipant AddParticipant(ExecPlanDbContext ctx, Guid activationId, Guid teamId, string teamName, ParticipantStatus status)
    {
        var p = new ActivationParticipant
        {
            ActivationId = activationId, UserId = Guid.NewGuid(), TeamId = teamId,
            TeamNameSnapshot = teamName, Status = status,
        };
        ctx.Add(p);
        return p;
    }

    private static ExecutionTask MakeTask(Guid activationId, Guid participantId, string title, ExecTaskStatus status) => new()
    {
        ActivationId = activationId, ParticipantId = participantId, Title = title, Order = 1,
        Status = status, DueAtUtc = new DateTime(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc),
    };

    private static void AddTask(ExecPlanDbContext ctx, Guid activationId, Guid participantId, string title, ExecTaskStatus status)
    {
        var t = MakeTask(activationId, participantId, title, status);
        if (status == ExecTaskStatus.Done)
        {
            t.CompletedAtUtc = new DateTime(2026, 6, 30, 9, 0, 0, DateTimeKind.Utc);
        }
        ctx.Add(t);
    }
}
