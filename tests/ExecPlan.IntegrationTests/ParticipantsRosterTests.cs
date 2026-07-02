using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Auth;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Phase 3 Part A / gap G5 — participant roster. <c>set-substitute</c> and task-reassign both require a
/// <c>participantId</c> that NO read endpoint previously returned (a client only ever saw its own via
/// my-tasks), so a leader/manager UI could not enumerate participants to act on.
/// <see cref="ExecPlan.Api.Controllers.ActivationsController.Participants"/> returns the roster with the
/// ids those operations need, leader-scoped to led teams (same object-level rule as the dashboard,
/// DEC-17), manager/admin see all, members are 403.
/// </summary>
[Collection("WebHostSequential")]
public class ParticipantsRosterTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public ParticipantsRosterTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Roster-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record RosterRowDto(
        Guid ParticipantId, Guid UserId, string FullName, Guid TeamId, string TeamName,
        ParticipantStatus Status, bool IsSubstitute, Guid? InductedFromParticipantId, int TasksTotal, int TasksDone);

    private sealed record Seed(
        Guid ActivationId, Guid ParticipantAlphaId, string ManagerUserName, string LeaderAlphaUserName,
        string LeaderBetaUserName, string LeaderGammaUserName, string MemberAlphaUserName);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    /// <summary>Plan with teams Alpha (leaderAlpha) &amp; Beta (leaderBeta) participating, plus team Gamma
    /// (leaderGamma) that has NO participant in this activation. One task on the Alpha participant.</summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = new IdentityPasswordHasher();

        var org = new Organization { Name = $"Roster-Org-{Guid.NewGuid():N}" };
        ctx.Organizations.Add(org);

        User MakeUser(UserRole role, string label) => new()
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            FullName = label,
            Phone = "+96500000000",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            OrganizationId = org.Id,
            IsActive = true,
        };

        var manager = MakeUser(UserRole.PlanManager, "roster-mgr");
        var leaderAlpha = MakeUser(UserRole.TeamLeader, "roster-leader-alpha");
        var leaderBeta = MakeUser(UserRole.TeamLeader, "roster-leader-beta");
        var leaderGamma = MakeUser(UserRole.TeamLeader, "roster-leader-gamma");
        var memberAlpha = MakeUser(UserRole.TeamMember, "roster-member-alpha");
        var memberBeta = MakeUser(UserRole.TeamMember, "roster-member-beta");
        ctx.Users.AddRange(manager, leaderAlpha, leaderBeta, leaderGamma, memberAlpha, memberBeta);

        var plan = new Plan { Name = "Roster Plan", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
        ctx.Plans.Add(plan);

        var teamAlpha = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leaderAlpha.Id };
        var teamBeta = new Team { PlanId = plan.Id, Name = "Beta", TeamLeaderUserId = leaderBeta.Id };
        var teamGamma = new Team { PlanId = plan.Id, Name = "Gamma", TeamLeaderUserId = leaderGamma.Id };
        ctx.Teams.AddRange(teamAlpha, teamBeta, teamGamma);

        var activation = new PlanActivation
        {
            PlanId = plan.Id,
            Status = ActivationStatus.Active,
            Shift = _factory.FixedShift.Band,
            RosterDate = _factory.FixedShift.RosterDate,
            ActivatedByUserId = manager.Id,
            ActivatedAtUtc = _factory.FixedUtcNow,
            EscalationThreshold = 5,
        };
        ctx.PlanActivations.Add(activation);

        var pAlpha = new ActivationParticipant
        {
            ActivationId = activation.Id, UserId = memberAlpha.Id, TeamId = teamAlpha.Id,
            TeamNameSnapshot = teamAlpha.Name, Status = ParticipantStatus.Pending,
        };
        var pBeta = new ActivationParticipant
        {
            ActivationId = activation.Id, UserId = memberBeta.Id, TeamId = teamBeta.Id,
            TeamNameSnapshot = teamBeta.Name, Status = ParticipantStatus.Ready,
        };
        ctx.ActivationParticipants.AddRange(pAlpha, pBeta);

        ctx.ExecutionTasks.Add(new ExecutionTask
        {
            ActivationId = activation.Id, ParticipantId = pAlpha.Id, Title = "Secure the gate",
            Order = 0, Status = ExecTaskStatus.Pending, DueAtUtc = _factory.FixedUtcNow.AddHours(1),
        });

        ctx.SaveChanges();

        return new Seed(
            activation.Id, pAlpha.Id, manager.UserName, leaderAlpha.UserName,
            leaderBeta.UserName, leaderGamma.UserName, memberAlpha.UserName);
    }

    [Fact]
    public async Task Manager_sees_all_participants_with_ids_and_task_counts()
    {
        var seed = SeedScenario();
        var manager = await LoginAsAsync(seed.ManagerUserName, Password);

        var roster = await manager.GetFromJsonAsync<List<RosterRowDto>>(
            $"/api/v1/activations/{seed.ActivationId}/participants");

        roster.Should().HaveCount(2);
        var alpha = roster!.Single(r => r.ParticipantId == seed.ParticipantAlphaId);
        alpha.TeamName.Should().Be("Alpha");
        alpha.FullName.Should().Be("roster-member-alpha");
        alpha.TasksTotal.Should().Be(1);
        alpha.TasksDone.Should().Be(0);
    }

    [Fact]
    public async Task Leader_sees_only_participants_of_their_led_teams()
    {
        var seed = SeedScenario();
        var leaderAlpha = await LoginAsAsync(seed.LeaderAlphaUserName, Password);

        var roster = await leaderAlpha.GetFromJsonAsync<List<RosterRowDto>>(
            $"/api/v1/activations/{seed.ActivationId}/participants");

        roster.Should().ContainSingle();
        var only = roster!.Single();
        only.ParticipantId.Should().Be(seed.ParticipantAlphaId);
        only.TeamName.Should().Be("Alpha");
    }

    [Fact]
    public async Task Leader_of_an_unrelated_team_gets_403()
    {
        var seed = SeedScenario();
        var leaderGamma = await LoginAsAsync(seed.LeaderGammaUserName, Password);

        var response = await leaderGamma.GetAsync($"/api/v1/activations/{seed.ActivationId}/participants");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_gets_403()
    {
        var seed = SeedScenario();
        var member = await LoginAsAsync(seed.MemberAlphaUserName, Password);

        var response = await member.GetAsync($"/api/v1/activations/{seed.ActivationId}/participants");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
