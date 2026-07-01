using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Task 18 review follow-up (Important, inherited): the dashboard GET endpoint's
/// <c>[Authorize(Roles = "SystemAdmin,PlanManager,TeamLeader")]</c> gate is role-only — it does not by
/// itself stop a TeamLeader from reading the dashboard of an activation none of their teams participate
/// in. <see cref="ExecPlan.Api.Controllers.ActivationsController.Dashboard"/> now adds an object-level
/// check (DEC-17, PRD §14 "own teams"): a TeamLeader must lead at least one team that has a participant
/// in the activation, or the request 403s. Manager/Admin remain unscoped. This file proves all three
/// outcomes end-to-end over real HTTP against the hosted API.
/// </summary>
[Collection("WebHostSequential")]
public class DashboardAccessTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public DashboardAccessTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Dashboard-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record Seed(Guid ActivationId, string ManagerUserName, string LeaderAlphaUserName, string LeaderBetaUserName);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    /// <summary>
    /// One plan, two teams (Alpha led by leaderAlpha, Beta led by leaderBeta), one Active activation
    /// whose only participant belongs to team Alpha. So leaderAlpha "leads a participating team",
    /// leaderBeta leads an entirely unrelated team, and the manager (plan creator) can see it regardless.
    /// </summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();

        var org = new Organization { Name = $"Dash-Org-{Guid.NewGuid():N}" };
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

        var manager = MakeUser(UserRole.PlanManager, "dash-mgr");
        var leaderAlpha = MakeUser(UserRole.TeamLeader, "dash-leader-alpha");
        var leaderBeta = MakeUser(UserRole.TeamLeader, "dash-leader-beta");
        var member = MakeUser(UserRole.TeamMember, "dash-member");
        ctx.Users.AddRange(manager, leaderAlpha, leaderBeta, member);

        var plan = new Plan
        {
            Name = "Dashboard Access Plan",
            Type = PlanType.Guard,
            Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Plans.Add(plan);

        var teamAlpha = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leaderAlpha.Id };
        var teamBeta = new Team { PlanId = plan.Id, Name = "Beta", TeamLeaderUserId = leaderBeta.Id };
        ctx.Teams.AddRange(teamAlpha, teamBeta);

        var activation = new PlanActivation
        {
            PlanId = plan.Id,
            Status = ActivationStatus.Active,
            Shift = ShiftBand.Morning,
            RosterDate = DateTime.UtcNow.Date,
            ActivatedByUserId = manager.Id,
            ActivatedAtUtc = DateTime.UtcNow,
            EscalationThreshold = 5,
        };
        ctx.PlanActivations.Add(activation);

        // Only team Alpha participates — team Beta (and its leader) has no stake in this activation.
        ctx.ActivationParticipants.Add(new ActivationParticipant
        {
            ActivationId = activation.Id,
            UserId = member.Id,
            TeamId = teamAlpha.Id,
            TeamNameSnapshot = teamAlpha.Name,
            Status = ParticipantStatus.Pending,
        });

        ctx.SaveChanges();

        return new Seed(activation.Id, manager.UserName, leaderAlpha.UserName, leaderBeta.UserName);
    }

    [Fact]
    public async Task Leader_of_a_participating_team_gets_200()
    {
        var seed = SeedScenario();
        var leaderAlpha = await LoginAsAsync(seed.LeaderAlphaUserName, Password);

        var response = await leaderAlpha.GetAsync($"/api/v1/activations/{seed.ActivationId}/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Leader_of_an_unrelated_team_gets_403()
    {
        var seed = SeedScenario();
        var leaderBeta = await LoginAsAsync(seed.LeaderBetaUserName, Password);

        var response = await leaderBeta.GetAsync($"/api/v1/activations/{seed.ActivationId}/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_gets_200_regardless_of_team_leadership()
    {
        var seed = SeedScenario();
        var manager = await LoginAsAsync(seed.ManagerUserName, Password);

        var response = await manager.GetAsync($"/api/v1/activations/{seed.ActivationId}/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
