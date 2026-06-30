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
/// End-to-end real-time proof using a REAL <see cref="HubConnection"/> (not a test double): a member
/// connects to <c>/hubs/dashboard</c> over the in-memory <see cref="TestAppFactory.Server"/> (long-polling
/// transport via <c>CreateHandler()</c>, JWT supplied by the <c>AccessTokenProvider</c>), joins their
/// activation's group, then triggers a state change over HTTP (<c>acknowledge</c>) and receives the
/// <c>DashboardUpdated</c> push that <see cref="ExecPlan.Api.Hubs.SignalRRealtimeNotifier"/> sends after
/// the commit. This exercises the hub authorization, group membership, and notifier push together.
/// </summary>
public class RealtimeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public RealtimeTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Realtime-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record Seed(Guid ActivationId, string MemberUserName);

    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();

        var org = new Organization { Name = $"Realtime-Org-{Guid.NewGuid():N}" };
        ctx.Organizations.Add(org);

        var member = new User
        {
            UserName = $"rt-member-{Guid.NewGuid():N}",
            FullName = "Realtime Member",
            Phone = "+96500000000",
            PasswordHash = hasher.Hash(Password),
            Role = UserRole.TeamMember,
            OrganizationId = org.Id,
            IsActive = true,
        };
        ctx.Users.Add(member);

        var plan = new Plan
        {
            Name = "Realtime Plan",
            Type = PlanType.Guard,
            Status = PlanStatus.Ready,
            CreatedByUserId = member.Id,
        };
        ctx.Plans.Add(plan);

        var team = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = Guid.NewGuid() };
        ctx.Teams.Add(team);

        var activation = new PlanActivation
        {
            PlanId = plan.Id,
            Status = ActivationStatus.Active,
            Shift = ShiftBand.Morning,
            RosterDate = DateTime.UtcNow.Date,
            ActivatedByUserId = member.Id,
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

        return new Seed(activation.Id, member.UserName);
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

    [Fact]
    public async Task Joining_and_acknowledging_pushes_DashboardUpdated_to_the_client()
    {
        var seed = SeedScenario();
        var (httpClient, token) = await LoginAsync(seed.MemberUserName);

        var received = new TaskCompletionSource<DashboardDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/dashboard"), options =>
            {
                // The in-memory TestServer handler supports HTTP long polling (not raw WebSockets).
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        connection.On<DashboardDto>("DashboardUpdated", dto => received.TrySetResult(dto));

        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("JoinActivation", seed.ActivationId);

            // Trigger a committed state change over HTTP — the notifier pushes to the act-{id} group.
            var ack = await httpClient.PostAsync($"/api/v1/activations/{seed.ActivationId}/acknowledge", content: null);
            ack.StatusCode.Should().Be(HttpStatusCode.OK);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(received.Task, "the connected client should receive a DashboardUpdated push within 10s");

            var dto = await received.Task;
            dto.ActivationId.Should().Be(seed.ActivationId);
            dto.ReadyCount.Should().Be(1, "the acknowledging member is now Ready");
        }
        finally
        {
            await connection.DisposeAsync();
            httpClient.Dispose();
        }
    }

    [Fact]
    public async Task Joining_an_activation_the_caller_cannot_view_throws()
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
        var (httpClient, token) = await LoginAsync(seed.MemberUserName);

        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/dashboard"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        try
        {
            await connection.StartAsync();

            var act = async () => await connection.InvokeAsync("JoinActivation", foreignActivationId);

            await act.Should().ThrowAsync<HubException>();
        }
        finally
        {
            await connection.DisposeAsync();
            httpClient.Dispose();
        }
    }
}
