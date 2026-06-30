using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Auth;
using ExecPlan.Cli;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Task 21: the idempotent dev/eval <see cref="DataSeeder"/>. Each test builds its OWN throwaway
/// SQLite database (a private <see cref="SqliteFixture"/>, not the class-shared one) because
/// <see cref="DataSeeder"/> uses FIXED, known usernames — sharing a database across test methods would
/// make the second test's seed attempt a no-op via the very idempotency guard under test, which would
/// make assertions about "first-time seeding" order-dependent and flaky.
/// </summary>
public class SeedTests
{
    // 2026-06-30 08:00 UTC = 11:00 Asia/Kuwait → Morning band, roster 2026-06-30 (same convention as
    // TestAppFactory/EscalationServiceTests' fixed clocks elsewhere in this suite).
    private static readonly DateTime FixedUtcNow = new(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a real DI container the same way Program.cs does (AddInfrastructure +
    /// AddApplication), rebound onto the given fixture's shared connection with a fixed clock — same
    /// technique as <c>CliEscalationTests.BuildContainer</c>/<c>TestAppFactory</c>.</summary>
    private static ServiceProvider BuildContainer(SqliteFixture fx)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Jwt:Issuer"] = "execplan-tests",
                ["Jwt:Audience"] = "execplan-tests",
                ["Jwt:SigningKey"] = "integration-test-signing-key-needs-32-chars-minimum-0123456789",
                ["Jwt:AccessTokenMinutes"] = "30",
                ["Jwt:RefreshTokenDays"] = "14",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg); // JwtTokenFactory takes IConfiguration via DI.
        services.AddInfrastructure(cfg);
        services.AddApplication(cfg);
        services.AddScoped<ICurrentUser, NoOpCurrentUser>();

        services.RemoveAll<DbContextOptions<ExecPlanDbContext>>();
        services.RemoveAll<ExecPlanDbContext>();
        services.AddDbContext<ExecPlanDbContext>(o => o.UseSqlite(fx.Connection));

        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(new TestClock { UtcNow = FixedUtcNow });

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SeedAsync_creates_one_known_user_per_role_with_a_real_hashed_password()
    {
        using var fx = new SqliteFixture();
        await using var sp = BuildContainer(fx);

        await DataSeeder.SeedAsync(sp);

        await using var ctx = fx.NewContext();
        var users = ctx.Set<User>().ToList();
        users.Should().HaveCount(5); // admin, manager, leader, member, + the frozen substitute account

        var hasher = sp.GetRequiredService<IPasswordHasher>();

        var admin = users.Single(u => u.UserName == DataSeeder.AdminUserName);
        admin.Role.Should().Be(UserRole.SystemAdmin);
        admin.IsActive.Should().BeTrue();
        admin.PasswordHash.Should().NotBe(DataSeeder.DemoPassword); // actually hashed, never plaintext
        hasher.Verify(admin.PasswordHash, DataSeeder.DemoPassword).Should().BeTrue();

        var manager = users.Single(u => u.UserName == DataSeeder.ManagerUserName);
        manager.Role.Should().Be(UserRole.PlanManager);
        hasher.Verify(manager.PasswordHash, DataSeeder.DemoPassword).Should().BeTrue();

        var leader = users.Single(u => u.UserName == DataSeeder.LeaderUserName);
        leader.Role.Should().Be(UserRole.TeamLeader);
        hasher.Verify(leader.PasswordHash, DataSeeder.DemoPassword).Should().BeTrue();

        var member = users.Single(u => u.UserName == DataSeeder.MemberUserName);
        member.Role.Should().Be(UserRole.TeamMember);
        hasher.Verify(member.PasswordHash, DataSeeder.DemoPassword).Should().BeTrue();
    }

    [Fact]
    public async Task Seeded_manager_can_log_in_through_the_real_IAuthService()
    {
        using var fx = new SqliteFixture();
        await using var sp = BuildContainer(fx);
        await DataSeeder.SeedAsync(sp);

        await using var ctx = fx.NewContext();
        var manager = ctx.Set<User>().Single(u => u.UserName == DataSeeder.ManagerUserName);

        using var scope = sp.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();

        var tokens = await auth.LoginAsync(DataSeeder.ManagerUserName, DataSeeder.DemoPassword);
        tokens.UserId.Should().Be(manager.Id);
        tokens.Role.Should().Be(UserRole.PlanManager);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();

        var wrongPassword = async () => await auth.LoginAsync(DataSeeder.ManagerUserName, "wrong-password");
        await wrongPassword.Should().ThrowAsync<ExecPlan.Application.Common.AppException>();
    }

    [Fact]
    public async Task SeedAsync_creates_the_showcase_plan_with_two_teams_templates_and_a_substitute_roster()
    {
        using var fx = new SqliteFixture();
        await using var sp = BuildContainer(fx);
        await DataSeeder.SeedAsync(sp);

        await using var ctx = fx.NewContext();

        var plan = ctx.Set<Plan>().Single();
        plan.Status.Should().Be(PlanStatus.Ready);

        var manager = ctx.Set<User>().Single(u => u.UserName == DataSeeder.ManagerUserName);
        plan.CreatedByUserId.Should().Be(manager.Id);

        // manager is registered as an authorized activator (in ADDITION to being the creator).
        ctx.Set<PlanActivator>().Should().ContainSingle(a => a.PlanId == plan.Id && a.UserId == manager.Id);

        var teams = ctx.Set<Team>().Where(t => t.PlanId == plan.Id).ToList();
        teams.Should().HaveCount(2);

        var leader = ctx.Set<User>().Single(u => u.UserName == DataSeeder.LeaderUserName);
        var ledTeam = teams.Should().ContainSingle(t => t.TeamLeaderUserId == leader.Id).Subject;

        var teamIds = teams.Select(t => t.Id).ToList();
        var templates = ctx.Set<TaskTemplate>().Where(tt => teamIds.Contains(tt.TeamId)).ToList();
        templates.Should().HaveCountGreaterThanOrEqualTo(2);
        templates.Should().Contain(t => t.TeamId == ledTeam.Id);

        var member = ctx.Set<User>().Single(u => u.UserName == DataSeeder.MemberUserName);
        var substitute = ctx.Set<User>().Single(u => u.UserName != DataSeeder.AdminUserName
            && u.UserName != DataSeeder.ManagerUserName
            && u.UserName != DataSeeder.LeaderUserName
            && u.UserName != DataSeeder.MemberUserName);

        var roster = ctx.Set<ShiftAssignment>().Where(sa => sa.TeamId == ledTeam.Id).ToList();
        roster.Should().Contain(sa => sa.UserId == member.Id && sa.SubstituteForUserId == null);
        roster.Should().Contain(sa => sa.UserId == leader.Id && sa.SubstituteForUserId == null);

        // The designated substitute row: frozen-in as the stand-in for `member`.
        var substituteRow = roster.Should().ContainSingle(sa => sa.SubstituteForUserId == member.Id).Subject;
        substituteRow.UserId.Should().Be(substitute.Id);
    }

    [Fact]
    public async Task SeedAsync_called_twice_does_not_duplicate_any_seeded_row()
    {
        using var fx = new SqliteFixture();

        await using (var sp1 = BuildContainer(fx))
        {
            await DataSeeder.SeedAsync(sp1);
        }

        // Simulates a second app start against the SAME underlying database.
        await using (var sp2 = BuildContainer(fx))
        {
            await DataSeeder.SeedAsync(sp2);
        }

        await using var ctx = fx.NewContext();
        ctx.Set<User>().Count().Should().Be(5);
        ctx.Set<User>().Count(u => u.UserName == DataSeeder.AdminUserName).Should().Be(1);
        ctx.Set<User>().Count(u => u.UserName == DataSeeder.ManagerUserName).Should().Be(1);
        ctx.Set<User>().Count(u => u.UserName == DataSeeder.LeaderUserName).Should().Be(1);
        ctx.Set<User>().Count(u => u.UserName == DataSeeder.MemberUserName).Should().Be(1);
        ctx.Set<Plan>().Count().Should().Be(1);
        ctx.Set<Team>().Count().Should().Be(2);
        ctx.Set<PlanActivator>().Count().Should().Be(1);
    }

    /// <summary>
    /// Proves the seeded plan is runnable on first boot, regardless of what time the app happened to
    /// start: activating it (as the seeded `manager`) produces participants for exactly the on-duty
    /// roster row(s) DataSeeder wrote against the clock's CURRENT shift, with the substitute correctly
    /// resolved — i.e. the full Task 18 acceptance flow has real data to run against from minute one.
    /// </summary>
    [Fact]
    public async Task The_seeded_plan_activates_successfully_and_resolves_the_frozen_substitute()
    {
        using var fx = new SqliteFixture();
        await using var sp = BuildContainer(fx);
        await DataSeeder.SeedAsync(sp);

        await using var ctx = fx.NewContext();
        var plan = ctx.Set<Plan>().Single();
        var manager = ctx.Set<User>().Single(u => u.UserName == DataSeeder.ManagerUserName);
        var leader = ctx.Set<User>().Single(u => u.UserName == DataSeeder.LeaderUserName);
        var member = ctx.Set<User>().Single(u => u.UserName == DataSeeder.MemberUserName);
        var substitute = ctx.Set<User>().Single(u => u.UserName != DataSeeder.AdminUserName
            && u.UserName != DataSeeder.ManagerUserName
            && u.UserName != DataSeeder.LeaderUserName
            && u.UserName != DataSeeder.MemberUserName);

        using var scope = sp.CreateScope();
        var activationService = scope.ServiceProvider.GetRequiredService<IActivationService>();
        var activationId = await activationService.ActivateAsync(plan.Id, manager.Id);
        activationId.Should().NotBe(Guid.Empty);

        await using var verifyCtx = fx.NewContext();
        var participants = verifyCtx.Set<ActivationParticipant>()
            .Where(p => p.ActivationId == activationId).ToList();

        // Only the led team has an on-duty roster (leader + member); the second team has templates
        // but no roster, so it contributes zero participants — that's fine, ActivationService only
        // requires SOME team to be on duty.
        participants.Should().HaveCount(2);
        participants.Should().Contain(p => p.UserId == leader.Id);

        var memberParticipant = participants.Single(p => p.UserId == member.Id);
        memberParticipant.ResolvedSubstituteUserId.Should().Be(substitute.Id);

        var tasks = verifyCtx.Set<ExecutionTask>()
            .Where(t => t.ParticipantId == memberParticipant.Id).ToList();
        tasks.Should().NotBeEmpty(); // generated fresh from the led team's task templates.
    }
}
