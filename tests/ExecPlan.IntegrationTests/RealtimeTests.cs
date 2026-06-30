using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Dashboard;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// End-to-end real-time proof using a REAL <see cref="HubConnection"/> (not a test double) against
/// <c>/hubs/dashboard</c> over the in-memory <see cref="TestAppFactory.Server"/> (long-polling transport
/// via <c>CreateHandler()</c>, JWT supplied by the <c>AccessTokenProvider</c>).
///
/// Task 19 review (Important, DEC-18): the hub's join-gate previously admitted ANY participant of the
/// activation, including a plain <c>TeamMember</c> — letting a member receive the full cross-team
/// <see cref="DashboardDto"/> over SignalR even though PRD §14 and the REST dashboard endpoint
/// (<see cref="DashboardAccessTests"/>) both 403 members. <see cref="ExecPlan.Api.Hubs.DashboardHub"/>'s
/// gate now matches the REST gate exactly: SystemAdmin/PlanManager always; TeamLeader only for an
/// activation in which a team they lead participates; plain TeamMember never (even when they ARE a
/// participant). The realistic push flow is therefore manager-watches/member-acts: a PlanManager opens
/// the connection and joins, a member acknowledges over HTTP, and the manager receives the
/// <c>DashboardUpdated</c> push.
/// </summary>
public class RealtimeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public RealtimeTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Realtime-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record Seed(Guid ActivationId, string ManagerUserName, string LeaderUserName, string MemberUserName);

    /// <summary>One plan, one team (Alpha, led by leader) participating in one Active activation whose
    /// only participant is the member (a TeamMember on team Alpha). The manager is the plan creator.</summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();

        var org = new Organization { Name = $"Realtime-Org-{Guid.NewGuid():N}" };
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

        var manager = MakeUser(UserRole.PlanManager, "rt-manager");
        var leader = MakeUser(UserRole.TeamLeader, "rt-leader");
        var member = MakeUser(UserRole.TeamMember, "rt-member");
        ctx.Users.AddRange(manager, leader, member);

        var plan = new Plan
        {
            Name = "Realtime Plan",
            Type = PlanType.Guard,
            Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Plans.Add(plan);

        var team = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = leader.Id };
        ctx.Teams.Add(team);

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

        ctx.ActivationParticipants.Add(new ActivationParticipant
        {
            ActivationId = activation.Id,
            UserId = member.Id,
            TeamId = team.Id,
            TeamNameSnapshot = team.Name,
            Status = ParticipantStatus.Pending,
        });

        ctx.SaveChanges();

        return new Seed(activation.Id, manager.UserName, leader.UserName, member.UserName);
    }

    private async Task<(HttpClient Client, string Token)> LoginAsync(string userName)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, tokens.AccessToken);
    }

    private HubConnection BuildConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/dashboard"), options =>
            {
                // The in-memory TestServer handler supports HTTP long polling (not raw WebSockets).
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    [Fact]
    public async Task Manager_watching_receives_DashboardUpdated_when_a_member_acknowledges()
    {
        var seed = SeedScenario();
        var (managerClient, managerToken) = await LoginAsync(seed.ManagerUserName);
        var (memberClient, _) = await LoginAsync(seed.MemberUserName);

        var received = new TaskCompletionSource<DashboardDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = BuildConnection(managerToken);
        connection.On<DashboardDto>("DashboardUpdated", dto => received.TrySetResult(dto));

        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("JoinActivation", seed.ActivationId);

            // The member acknowledges over HTTP — they don't (and now can't) join the dashboard group
            // themselves, but their state change still triggers the push to whoever IS in the group.
            var ack = await memberClient.PostAsync($"/api/v1/activations/{seed.ActivationId}/acknowledge", content: null);
            ack.StatusCode.Should().Be(HttpStatusCode.OK);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(received.Task, "the connected manager should receive a DashboardUpdated push within 10s");

            var dto = await received.Task;
            dto.ActivationId.Should().Be(seed.ActivationId);
            dto.ReadyCount.Should().Be(1, "the acknowledging member is now Ready");
        }
        finally
        {
            await connection.DisposeAsync();
            managerClient.Dispose();
            memberClient.Dispose();
        }
    }

    [Fact]
    public async Task TeamLeader_of_a_participating_team_can_join()
    {
        var seed = SeedScenario();
        var (_, leaderToken) = await LoginAsync(seed.LeaderUserName);

        var connection = BuildConnection(leaderToken);

        try
        {
            await connection.StartAsync();

            var act = async () => await connection.InvokeAsync("JoinActivation", seed.ActivationId);

            await act.Should().NotThrowAsync("a TeamLeader of a team participating in the activation may watch its dashboard (DEC-17/DEC-18)");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Plain_TeamMember_participant_is_rejected_even_though_they_ARE_a_participant()
    {
        // Task 19 fix: PRD §14 lists "View live dashboard: Member –" and the REST endpoint already 403s
        // members. The hub must reject the SAME caller it used to admit, even though they really are a
        // participant of this exact activation.
        var seed = SeedScenario();
        var (_, memberToken) = await LoginAsync(seed.MemberUserName);

        var connection = BuildConnection(memberToken);

        try
        {
            await connection.StartAsync();

            var act = async () => await connection.InvokeAsync("JoinActivation", seed.ActivationId);

            await act.Should().ThrowAsync<HubException>();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task Joining_an_activation_the_caller_is_not_a_participant_of_throws()
    {
        // An activation that exists, but the member is NOT a participant of and leads no team in.
        Guid foreignActivationId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var activation = new PlanActivation
            {
                PlanId = Guid.NewGuid(),
                Status = ActivationStatus.Active,
                Shift = ShiftBand.Morning,
                RosterDate = DateTime.UtcNow.Date,
                ActivatedByUserId = Guid.NewGuid(),
                ActivatedAtUtc = DateTime.UtcNow,
                EscalationThreshold = 5,
            };
            ctx.PlanActivations.Add(activation);
            ctx.SaveChanges();
            foreignActivationId = activation.Id;
        }

        var seed = SeedScenario();
        var (_, memberToken) = await LoginAsync(seed.MemberUserName);

        var connection = BuildConnection(memberToken);

        try
        {
            await connection.StartAsync();

            var act = async () => await connection.InvokeAsync("JoinActivation", foreignActivationId);

            await act.Should().ThrowAsync<HubException>();
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
