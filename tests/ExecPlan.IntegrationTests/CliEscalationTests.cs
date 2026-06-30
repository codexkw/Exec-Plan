using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Common;
using ExecPlan.Application.Escalation;
using ExecPlan.Application.Execution;
using ExecPlan.Application.Shifts;
using ExecPlan.Cli;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Proves FR-ESC-1 ("identical behavior whether triggered from the dashboard or the CLI"): the same
/// <see cref="EscalationRunner"/> the <c>ExecPlan.Cli</c> <c>Program.cs</c> drives is exercised here
/// over a real DI container (built the same way <c>Program.cs</c> builds it: <c>AddInfrastructure</c>
/// + <c>AddApplication</c>) wired to the shared <see cref="SqliteFixture"/> connection — no process
/// spawn required. Two structurally-identical scenarios are seeded; one is escalated by calling
/// <see cref="IEscalationService.RunCycleAsync"/> directly, the other via the CLI's
/// <see cref="EscalationRunner"/>, and the resulting cycle counts and persisted state are asserted to
/// match exactly.
/// </summary>
public class CliEscalationTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public CliEscalationTests(SqliteFixture fx) => _fx = fx;

    private static readonly DateTime MorningUtc = KwtToUtc(2026, 6, 30, 9, 0);
    private static readonly DateTime RosterDate = new(2026, 6, 30);

    private static DateTime KwtToUtc(int y, int mo, int d, int h, int mi)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuwait");
        return TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), tz);
    }

    private sealed record Seed(Guid PlanId, Guid ManagerId, Guid Member1Id, Guid Member2Id, Guid SubstituteId);

    /// <summary>Builds a real DI container the same way Program.cs does (AddInfrastructure + AddApplication),
    /// except the DbContext is rebound to the fixture's shared in-memory connection and the clock is fixed,
    /// mirroring TestAppFactory's host-rewiring pattern for a bare ServiceCollection.</summary>
    private ServiceProvider BuildContainer(int threshold)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Escalation:DefaultThreshold"] = threshold.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(cfg);
        services.AddApplication(cfg);
        services.AddScoped<ICurrentUser, NoOpCurrentUser>();

        // Rebind the DbContext registered by AddInfrastructure onto the fixture's shared connection
        // (same technique TestAppFactory uses for the hosted API), and fix the clock so shift
        // resolution is deterministic.
        services.RemoveAll<DbContextOptions<ExecPlanDbContext>>();
        services.RemoveAll<ExecPlanDbContext>();
        services.AddDbContext<ExecPlanDbContext>(o => o.UseSqlite(_fx.Connection));

        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(new TestClock { UtcNow = MorningUtc });

        return services.BuildServiceProvider();
    }

    /// <summary>Seeds a plan/team/two-templates and a Morning roster where `substitute` stands in for
    /// `member2` — same shape as <c>EscalationServiceTests.SeedScenario</c>.</summary>
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
            Name = $"P-{Guid.NewGuid():N}", Type = PlanType.Guard, Status = PlanStatus.Ready,
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

        return new Seed(plan.Id, manager.Id, member1.Id, member2.Id, substitute.Id);
    }

    private async Task<Guid> ActivateAsync(IServiceProvider sp, Guid planId, Guid managerId)
    {
        using var scope = sp.CreateScope();
        var activation = scope.ServiceProvider.GetRequiredService<IActivationService>();
        return await activation.ActivateAsync(planId, managerId);
    }

    [Fact]
    public async Task EscalationRunner_drives_the_same_cycle_as_calling_IEscalationService_directly()
    {
        // Two structurally identical scenarios, each on its own container instance (same shared
        // SQLite connection/schema) — one escalated via the direct service call, one via the CLI's
        // EscalationRunner. Both started at CallAttemptCount=1 by activation; threshold=5 keeps both
        // cycles below escalation so the comparison is a pure "did the same mutation happen" check.
        await using var directContainer = BuildContainer(threshold: 5);
        var directSeed = SeedScenario();
        var directActivationId = await ActivateAsync(directContainer, directSeed.PlanId, directSeed.ManagerId);

        await using var cliContainer = BuildContainer(threshold: 5);
        var cliSeed = SeedScenario();
        var cliActivationId = await ActivateAsync(cliContainer, cliSeed.PlanId, cliSeed.ManagerId);

        // Direct path: resolve IEscalationService straight from the container, as a dashboard
        // controller action would.
        EscalationCycleResult directResult;
        using (var scope = directContainer.CreateScope())
        {
            var escalation = scope.ServiceProvider.GetRequiredService<IEscalationService>();
            directResult = await escalation.RunCycleAsync(directActivationId);
        }

        // CLI path: the exact class Program.cs constructs and calls, over the same kind of container.
        var output = new StringWriter();
        var runner = new EscalationRunner(cliContainer);
        var exitCode = await runner.RunAsync(new EscalationArgs(cliActivationId, AllActive: false), output);

        exitCode.Should().Be(EscalationRunner.ExitSuccess);
        output.ToString().Should().Contain(cliActivationId.ToString());

        // Both Pending participants (member1, member2) get one more call attempt; nobody escalates yet.
        directResult.AttemptsAdded.Should().Be(2);
        directResult.Inducted.Should().Be(0);

        // The CLI-driven cycle produced the identical counts the direct call produced.
        await using var verifyCtx = _fx.NewContext();
        var cliParticipants = verifyCtx.Set<ActivationParticipant>()
            .Where(p => p.ActivationId == cliActivationId).ToList();
        cliParticipants.Should().HaveCount(2);
        cliParticipants.Should().OnlyContain(p => p.Status == ParticipantStatus.Pending);
        cliParticipants.Should().OnlyContain(p => p.CallAttemptCount == 2); // 1 -> 2, same as the direct path.

        var directParticipants = verifyCtx.Set<ActivationParticipant>()
            .Where(p => p.ActivationId == directActivationId).ToList();
        directParticipants.Select(p => p.CallAttemptCount)
            .Should().BeEquivalentTo(cliParticipants.Select(p => p.CallAttemptCount));

        // A CallAttempt row was added for the CLI-driven activation too (proves it went through the
        // real provider seam, not a stub).
        verifyCtx.Set<CallAttempt>().Count(c => c.ActivationId == cliActivationId).Should().Be(4); // 2 at activation + 2 this cycle.
    }

    [Fact]
    public async Task EscalationRunner_induces_a_substitute_through_the_CLI_path_just_like_the_service()
    {
        // threshold=2: activation seeds CallAttemptCount=1, so ONE escalation cycle tips a still-Pending
        // participant straight to Escalated + induction — exercising the full induction branch via the CLI.
        await using var container = BuildContainer(threshold: 2);
        var seed = SeedScenario();
        var activationId = await ActivateAsync(container, seed.PlanId, seed.ManagerId);

        // member1 acknowledges so only member2 stays Pending (mirrors EscalationServiceTests' scenario).
        using (var ackScope = container.CreateScope())
        {
            var acknowledge = ackScope.ServiceProvider.GetRequiredService<AcknowledgeService>();
            await acknowledge.AcknowledgeAsync(activationId, seed.Member1Id);
        }

        var output = new StringWriter();
        var runner = new EscalationRunner(container);
        var exitCode = await runner.RunAsync(new EscalationArgs(activationId, AllActive: false), output);

        exitCode.Should().Be(EscalationRunner.ExitSuccess);
        output.ToString().Should().Contain("attemptsAdded=1").And.Contain("inducted=1");

        await using var ctx = _fx.NewContext();
        var sub = ctx.Set<ActivationParticipant>().Single(p => p.ActivationId == activationId && p.IsSubstitute);
        sub.UserId.Should().Be(seed.SubstituteId);
        sub.Status.Should().Be(ParticipantStatus.Inducted);

        var member2Participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == seed.Member2Id && !p.IsSubstitute);
        member2Participant.Status.Should().Be(ParticipantStatus.Escalated);

        ctx.Set<EscalationLog>().Count(e => e.ActivationId == activationId).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_with_all_active_runs_every_active_activation()
    {
        await using var container = BuildContainer(threshold: 5);

        var seedA = SeedScenario();
        var activationA = await ActivateAsync(container, seedA.PlanId, seedA.ManagerId);
        var seedB = SeedScenario();
        var activationB = await ActivateAsync(container, seedB.PlanId, seedB.ManagerId);

        var output = new StringWriter();
        var runner = new EscalationRunner(container);
        var exitCode = await runner.RunAsync(new EscalationArgs(ActivationId: null, AllActive: true), output);

        exitCode.Should().Be(EscalationRunner.ExitSuccess);
        var text = output.ToString();
        text.Should().Contain(activationA.ToString());
        text.Should().Contain(activationB.ToString());

        await using var ctx = _fx.NewContext();
        ctx.Set<ActivationParticipant>().Where(p => p.ActivationId == activationA)
            .Should().OnlyContain(p => p.CallAttemptCount == 2);
        ctx.Set<ActivationParticipant>().Where(p => p.ActivationId == activationB)
            .Should().OnlyContain(p => p.CallAttemptCount == 2);
    }

    [Fact]
    public async Task RunAsync_returns_error_exit_code_for_an_unknown_activation()
    {
        await using var container = BuildContainer(threshold: 5);
        var output = new StringWriter();
        var runner = new EscalationRunner(container);

        var exitCode = await runner.RunAsync(new EscalationArgs(Guid.NewGuid(), AllActive: false), output);

        exitCode.Should().Be(EscalationRunner.ExitError);
        output.ToString().Should().Contain("NotFound");
    }

    [Fact]
    public void EscalationArgs_Parse_rejects_malformed_or_conflicting_input()
    {
        EscalationArgs.Parse(Array.Empty<string>()).Should().BeNull(); // neither flag
        EscalationArgs.Parse(new[] { "--all-active", "--activation", Guid.NewGuid().ToString() }).Should().BeNull(); // both
        EscalationArgs.Parse(new[] { "--activation", "not-a-guid" }).Should().BeNull();
        EscalationArgs.Parse(new[] { "--unknown-flag" }).Should().BeNull();

        var id = Guid.NewGuid();
        EscalationArgs.Parse(new[] { "--activation", id.ToString() }).Should().Be(new EscalationArgs(id, false));
        EscalationArgs.Parse(new[] { "--all-active" }).Should().Be(new EscalationArgs(null, true));
    }
}
