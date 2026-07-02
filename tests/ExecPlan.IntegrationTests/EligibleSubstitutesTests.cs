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
/// Phase 3 Part A / gap G5b (found in review) — <c>set-substitute</c> needs a substitute <em>userId</em>,
/// but the only member-listing endpoints (<c>team-members</c>, <c>teams</c>) are Manager/Admin-gated, so a
/// TeamLeader gets 403 and cannot populate a "who covers?" picker.
/// <see cref="ExecPlan.Api.Controllers.ActivationsController.EligibleSubstitutes"/> gives a leader (of that
/// team) or a manager the candidate pool: active members of the team who are not already on duty in the
/// activation.
/// </summary>
[Collection("WebHostSequential")]
public class EligibleSubstitutesTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public EligibleSubstitutesTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Subs-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record CandidateDto(Guid UserId, string FullName);

    private sealed record Seed(
        Guid ActivationId, Guid TeamAlphaId, Guid OnDutyUserId, Guid Candidate2Id, Guid Candidate3Id,
        string ManagerUserName, string LeaderAlphaUserName, string LeaderBetaUserName, string OnDutyMemberUserName);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    /// <summary>Team Alpha (leaderAlpha) has three members: one is already an active participant (on duty),
    /// the other two are eligible substitutes. Team Beta (leaderBeta) is unrelated.</summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = new IdentityPasswordHasher();

        var org = new Organization { Name = $"Subs-Org-{Guid.NewGuid():N}" };
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

        var manager = MakeUser(UserRole.PlanManager, "subs-mgr");
        var leaderAlpha = MakeUser(UserRole.TeamLeader, "subs-leader-alpha");
        var leaderBeta = MakeUser(UserRole.TeamLeader, "subs-leader-beta");
        var onDuty = MakeUser(UserRole.TeamMember, "subs-onduty");
        var cand2 = MakeUser(UserRole.TeamMember, "subs-cand2");
        var cand3 = MakeUser(UserRole.TeamMember, "subs-cand3");
        ctx.Users.AddRange(manager, leaderAlpha, leaderBeta, onDuty, cand2, cand3);

        var plan = new Plan { Name = "Subs Plan", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
        ctx.Plans.Add(plan);

        var teamAlpha = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leaderAlpha.Id };
        var teamBeta = new Team { PlanId = plan.Id, Name = "Beta", TeamLeaderUserId = leaderBeta.Id };
        ctx.Teams.AddRange(teamAlpha, teamBeta);

        // All three are members of team Alpha.
        ctx.TeamMemberships.AddRange(
            new TeamMembership { TeamId = teamAlpha.Id, UserId = onDuty.Id },
            new TeamMembership { TeamId = teamAlpha.Id, UserId = cand2.Id },
            new TeamMembership { TeamId = teamAlpha.Id, UserId = cand3.Id });

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

        // onDuty is already a participant → excluded from the candidate pool.
        ctx.ActivationParticipants.Add(new ActivationParticipant
        {
            ActivationId = activation.Id, UserId = onDuty.Id, TeamId = teamAlpha.Id,
            TeamNameSnapshot = teamAlpha.Name, Status = ParticipantStatus.Pending,
        });

        ctx.SaveChanges();

        return new Seed(
            activation.Id, teamAlpha.Id, onDuty.Id, cand2.Id, cand3.Id,
            manager.UserName, leaderAlpha.UserName, leaderBeta.UserName, onDuty.UserName);
    }

    [Fact]
    public async Task Leader_of_the_team_sees_eligible_substitutes_excluding_on_duty_members()
    {
        var seed = SeedScenario();
        var leader = await LoginAsAsync(seed.LeaderAlphaUserName, Password);

        var candidates = await leader.GetFromJsonAsync<List<CandidateDto>>(
            $"/api/v1/activations/{seed.ActivationId}/teams/{seed.TeamAlphaId}/eligible-substitutes");

        var ids = candidates!.Select(c => c.UserId).ToList();
        ids.Should().BeEquivalentTo(new[] { seed.Candidate2Id, seed.Candidate3Id });
        ids.Should().NotContain(seed.OnDutyUserId);
    }

    [Fact]
    public async Task Manager_sees_eligible_substitutes_for_any_team()
    {
        var seed = SeedScenario();
        var manager = await LoginAsAsync(seed.ManagerUserName, Password);

        var candidates = await manager.GetFromJsonAsync<List<CandidateDto>>(
            $"/api/v1/activations/{seed.ActivationId}/teams/{seed.TeamAlphaId}/eligible-substitutes");

        candidates!.Select(c => c.UserId).Should().BeEquivalentTo(new[] { seed.Candidate2Id, seed.Candidate3Id });
    }

    [Fact]
    public async Task Leader_of_a_different_team_gets_403()
    {
        var seed = SeedScenario();
        var leaderBeta = await LoginAsAsync(seed.LeaderBetaUserName, Password);

        var response = await leaderBeta.GetAsync(
            $"/api/v1/activations/{seed.ActivationId}/teams/{seed.TeamAlphaId}/eligible-substitutes");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_member_gets_403()
    {
        var seed = SeedScenario();
        var member = await LoginAsAsync(seed.OnDutyMemberUserName, Password);

        var response = await member.GetAsync(
            $"/api/v1/activations/{seed.ActivationId}/teams/{seed.TeamAlphaId}/eligible-substitutes");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
