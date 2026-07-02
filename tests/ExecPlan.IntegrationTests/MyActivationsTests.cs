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
/// Phase 3 Part A / gap G1 — activation discovery. Every other activation route needs a known
/// activation Guid; the ONLY prior source of one was the <c>POST plans/{id}/activate</c> response, which
/// only the activating manager ever sees. <see cref="ExecPlan.Api.Controllers.ActivationsController.Mine"/>
/// gives every authenticated role an in-band way to find the activation(s) they are called to:
/// a Member/Leader sees the activations they participate in (active + recently closed); a Manager/Admin
/// sees all activations (managers see all plans, per the locked decision). Proven end-to-end over HTTP.
/// </summary>
[Collection("WebHostSequential")]
public class MyActivationsTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public MyActivationsTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Mine-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record MyActivationDto(
        Guid ActivationId, Guid PlanId, string PlanName, ActivationStatus Status, ShiftBand Shift,
        DateTime RosterDate, string MyRole, DateTime StartedAtUtc, DateTime? ClosedAtUtc, Guid? MyParticipantId);

    private sealed record Seed(
        Guid ActivationId, Guid MemberParticipantId, string ManagerUserName, string LeaderUserName,
        string MemberUserName, string OutsiderUserName, string PlanName);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    /// <summary>One plan, team Alpha (led by a leader), one Active activation whose single participant is
    /// a member of team Alpha. Plus an "outsider" member who participates in nothing.</summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        var hasher = new IdentityPasswordHasher();

        var org = new Organization { Name = $"Mine-Org-{Guid.NewGuid():N}" };
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

        var manager = MakeUser(UserRole.PlanManager, "mine-mgr");
        var leader = MakeUser(UserRole.TeamLeader, "mine-leader");
        var member = MakeUser(UserRole.TeamMember, "mine-member");
        var outsider = MakeUser(UserRole.TeamMember, "mine-outsider");
        ctx.Users.AddRange(manager, leader, member, outsider);

        const string planName = "Mine Discovery Plan";
        var plan = new Plan
        {
            Name = planName,
            Type = PlanType.Guard,
            Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Plans.Add(plan);

        var teamAlpha = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leader.Id };
        ctx.Teams.Add(teamAlpha);

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

        var participant = new ActivationParticipant
        {
            ActivationId = activation.Id,
            UserId = member.Id,
            TeamId = teamAlpha.Id,
            TeamNameSnapshot = teamAlpha.Name,
            Status = ParticipantStatus.Pending,
        };
        ctx.ActivationParticipants.Add(participant);

        ctx.SaveChanges();

        return new Seed(
            activation.Id, participant.Id, manager.UserName, leader.UserName,
            member.UserName, outsider.UserName, planName);
    }

    [Fact]
    public async Task Member_sees_their_active_activation_as_participant()
    {
        var seed = SeedScenario();
        var member = await LoginAsAsync(seed.MemberUserName, Password);

        var result = await member.GetFromJsonAsync<List<MyActivationDto>>("/api/v1/activations/mine");

        result.Should().ContainSingle(a => a.ActivationId == seed.ActivationId);
        var row = result!.Single(a => a.ActivationId == seed.ActivationId);
        row.MyRole.Should().Be("Participant");
        row.MyParticipantId.Should().Be(seed.MemberParticipantId);
        row.Status.Should().Be(ActivationStatus.Active);
        row.PlanName.Should().Be(seed.PlanName);
    }

    [Fact]
    public async Task Non_participant_member_sees_empty()
    {
        var seed = SeedScenario();
        var outsider = await LoginAsAsync(seed.OutsiderUserName, Password);

        var result = await outsider.GetFromJsonAsync<List<MyActivationDto>>("/api/v1/activations/mine");

        result.Should().NotContain(a => a.ActivationId == seed.ActivationId);
    }

    [Fact]
    public async Task Leader_of_participating_team_sees_it_as_leader()
    {
        var seed = SeedScenario();
        var leader = await LoginAsAsync(seed.LeaderUserName, Password);

        var result = await leader.GetFromJsonAsync<List<MyActivationDto>>("/api/v1/activations/mine");

        var row = result!.Single(a => a.ActivationId == seed.ActivationId);
        row.MyRole.Should().Be("Leader");
    }

    [Fact]
    public async Task Manager_sees_the_activation_as_manager()
    {
        var seed = SeedScenario();
        var manager = await LoginAsAsync(seed.ManagerUserName, Password);

        var result = await manager.GetFromJsonAsync<List<MyActivationDto>>("/api/v1/activations/mine");

        var row = result!.Single(a => a.ActivationId == seed.ActivationId);
        row.MyRole.Should().Be("Manager");
    }

    [Fact]
    public async Task A_closed_activation_is_included_within_the_recency_window_and_excluded_outside_it()
    {
        // A member with two CLOSED activations: one closed 1h ago (inside the 12h window) and one
        // closed 13h ago (outside). Discovery must surface only the recent one.
        Guid inWindowId;
        Guid outOfWindowId;
        string memberUserName;

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = new IdentityPasswordHasher();

            var org = new Organization { Name = $"Mine-Recency-{Guid.NewGuid():N}" };
            ctx.Organizations.Add(org);

            var manager = new User { UserName = $"rec-mgr-{Guid.NewGuid():N}", FullName = "rec-mgr", Phone = "+96500000000", PasswordHash = hasher.Hash(Password), Role = UserRole.PlanManager, OrganizationId = org.Id, IsActive = true };
            var member = new User { UserName = $"rec-member-{Guid.NewGuid():N}", FullName = "rec-member", Phone = "+96500000000", PasswordHash = hasher.Hash(Password), Role = UserRole.TeamMember, OrganizationId = org.Id, IsActive = true };
            ctx.Users.AddRange(manager, member);

            var plan = new Plan { Name = "Recency Plan", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
            ctx.Plans.Add(plan);
            var team = new Team { PlanId = plan.Id, Name = "Rec", TeamLeaderUserId = null };
            ctx.Teams.Add(team);

            PlanActivation MakeClosed(double hoursAgo)
            {
                var a = new PlanActivation
                {
                    PlanId = plan.Id,
                    Status = ActivationStatus.Closed,
                    Shift = _factory.FixedShift.Band,
                    RosterDate = _factory.FixedShift.RosterDate,
                    ActivatedByUserId = manager.Id,
                    ActivatedAtUtc = _factory.FixedUtcNow.AddHours(-hoursAgo - 1),
                    ClosedAtUtc = _factory.FixedUtcNow.AddHours(-hoursAgo),
                    EscalationThreshold = 5,
                };
                ctx.PlanActivations.Add(a);
                ctx.ActivationParticipants.Add(new ActivationParticipant
                {
                    ActivationId = a.Id, UserId = member.Id, TeamId = team.Id,
                    TeamNameSnapshot = team.Name, Status = ParticipantStatus.Ready,
                });
                return a;
            }

            var inWindow = MakeClosed(1);
            var outOfWindow = MakeClosed(13);
            ctx.SaveChanges();

            inWindowId = inWindow.Id;
            outOfWindowId = outOfWindow.Id;
            memberUserName = member.UserName;
        }

        var client = await LoginAsAsync(memberUserName, Password);
        var result = await client.GetFromJsonAsync<List<MyActivationDto>>("/api/v1/activations/mine");

        result.Should().Contain(a => a.ActivationId == inWindowId);
        result.Should().NotContain(a => a.ActivationId == outOfWindowId);
    }
}
