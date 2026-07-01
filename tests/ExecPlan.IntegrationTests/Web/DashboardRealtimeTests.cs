using System.Net;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Dashboard;
using ExecPlan.Application.Execution;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 16: proves the approved additive hub-auth change (<c>DashboardHub</c> now accepts
/// <c>JwtBearerDefaults.AuthenticationScheme</c> OR <see cref="ExecPlan.Api.Auth.AuthPolicies.AdminCookieScheme"/>)
/// by driving a REAL <see cref="HubConnection"/> against <c>/hubs/dashboard</c> authenticated with the
/// SAME <c>AdminCookie</c> a real browser session on the MVC dashboard uses — not a JWT — mirroring
/// <see cref="ExecPlan.IntegrationTests.RealtimeTests"/>'s proven long-polling-over-<c>TestServer</c>
/// mechanics for the connection itself. The activation is arranged the same proven way
/// <see cref="DashboardTests"/> uses (seed plan+team+membership+on-duty <see cref="ShiftAssignment"/>
/// aligned to <see cref="TestAppFactory.FixedShift"/>, then the real <see cref="IActivationService.ActivateAsync"/>),
/// and the push is triggered by calling the real <see cref="AcknowledgeService"/> in a DI scope (the
/// SAME trigger <see cref="RealtimeTests"/> uses over HTTP) — <see cref="AcknowledgeService.AcknowledgeAsync"/>
/// calls <c>IRealtimeNotifier.DashboardChangedAsync</c> right after its single commit, which
/// <c>SignalRRealtimeNotifier</c> turns into the <c>"DashboardUpdated"</c> group push this test asserts on.
///
/// <para><b>Cookie-on-raw-TestServer-handler mechanics:</b> <see cref="WebTestHelpers.NewClient"/>'s
/// cookie container lives inside <c>WebApplicationFactory</c>'s own <c>HttpClient</c> handler chain, which
/// is a different object from the bare <see cref="Microsoft.AspNetCore.TestHost.TestServer.CreateHandler"/>
/// handler the <see cref="HubConnection"/> below talks to directly (same pattern <see cref="RealtimeTests"/>
/// uses for its JWT). So rather than relying on an automatic cookie jar, this test extracts the raw
/// <c>Set-Cookie</c> value the login POST returns and attaches it verbatim as a <c>Cookie</c> request
/// header via <see cref="HttpConnectionOptions.Headers"/> (added to every negotiate/poll/send request the
/// SignalR client issues, the generic analogue of the JWT test's <c>AccessTokenProvider</c>) — this is the
/// real cookie-authenticated path, not the documented HTTP-snapshot-only fallback.</para>
/// </summary>
public class DashboardRealtimeTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "dash-rt-manager";
    private const string MemberUserName = "dash-rt-member";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _memberId;

    public DashboardRealtimeTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "Dashboard Realtime Test Org");
        if (org is null)
        {
            org = new Organization { Name = "Dashboard Realtime Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Dashboard Realtime Manager", "+96500003001", UserRole.PlanManager);
        _memberId = EnsureUser(ctx, hasher, MemberUserName, "Dashboard Realtime Member", "+96500003002", UserRole.TeamMember);
    }

    private Guid EnsureUser(ExecPlanDbContext ctx, IPasswordHasher hasher, string userName, string fullName, string phone, UserRole role)
    {
        var user = ctx.Users.FirstOrDefault(u => u.UserName == userName);
        if (user is null)
        {
            user = new User
            {
                UserName = userName,
                PasswordHash = hasher.Hash(Password),
                FullName = fullName,
                Phone = phone,
                Role = role,
                OrganizationId = _orgId,
                IsActive = true,
            };
            ctx.Users.Add(user);
            ctx.SaveChanges();
        }

        return user.Id;
    }

    /// <summary>Same arrangement pattern as <see cref="DashboardTests.ArrangeActiveActivationAsync"/>: one
    /// Ready plan, one team with one on-duty member aligned to <see cref="TestAppFactory.FixedShift"/>,
    /// activated via the real <see cref="IActivationService"/>.</summary>
    private async Task<Guid> ArrangeActiveActivationAsync()
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

            var plan = new Plan
            {
                Name = "Dashboard Realtime Plan",
                Type = PlanType.Daily,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();

            var team = new Team { PlanId = plan.Id, Name = "Team Realtime", TeamLeaderUserId = null };
            ctx.Teams.Add(team);
            ctx.SaveChanges();

            ctx.TaskTemplates.Add(new TaskTemplate
            {
                TeamId = team.Id,
                Title = "Realtime Task",
                Order = 1,
                Duration = TimeSpan.FromMinutes(30),
            });

            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _memberId });
            ctx.ShiftAssignments.Add(new ShiftAssignment
            {
                TeamId = team.Id,
                UserId = _memberId,
                Shift = _factory.FixedShift.Band,
                Date = _factory.FixedShift.RosterDate,
                SubstituteForUserId = null,
            });

            ctx.SaveChanges();
            planId = plan.Id;
        }

        using var activateScope = _factory.Services.CreateScope();
        var activation = activateScope.ServiceProvider.GetRequiredService<IActivationService>();
        return await activation.ActivateAsync(planId, _managerId, CancellationToken.None);
    }

    /// <summary>The real counted readiness tap — the SAME trigger <see cref="DashboardTests"/> uses and
    /// the one production trigger <see cref="RealtimeTests"/> proves fires a "DashboardUpdated" push.</summary>
    private async Task AcknowledgeAsync(Guid activationId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var ack = scope.ServiceProvider.GetRequiredService<AcknowledgeService>();
        await ack.AcknowledgeAsync(activationId, userId, CancellationToken.None);
    }

    /// <summary>Logs in as <paramref name="userName"/> via the real cookie sign-in flow
    /// (<see cref="WebTestHelpers.PostFormAsync(HttpClient,string,string,IDictionary{string,string})"/>) and
    /// returns the raw <c>"name=value"</c> text of the <c>AdminCookie</c> auth cookie the server set, for
    /// forwarding onto a connection whose <see cref="HttpMessageHandler"/> is NOT this client's own cookie
    /// jar (see class docs).</summary>
    private async Task<string> LoginAndGetAuthCookieAsync(string userName)
    {
        var client = WebTestHelpers.NewClient(_factory);
        var res = await WebTestHelpers.PostFormAsync(client, "/admin/login", "/admin/login",
            new Dictionary<string, string> { ["UserName"] = userName, ["Password"] = Password });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful login redirects to /admin");

        var setCookies = res.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : new List<string>();
        // The antiforgery cookie (from the earlier GET the token-scrape performed) is typically not
        // re-issued on this POST since it was already valid; the AdminCookie sign-in cookie is what the
        // successful login response actually sets. Filter defensively in case both are ever present.
        var authSetCookie = setCookies.FirstOrDefault(v => !v.Contains("Antiforgery", StringComparison.OrdinalIgnoreCase));
        authSetCookie.Should().NotBeNull("a successful login must Set-Cookie the AdminCookie auth cookie");

        return authSetCookie!.Split(';')[0]; // "<cookie-name>=<value>"
    }

    private HubConnection BuildCookieConnection(string cookieHeader) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/dashboard"), options =>
            {
                // Same in-memory TestServer long-polling mechanics as RealtimeTests.BuildConnection —
                // raw WebSockets aren't supported by the TestServer handler. Instead of a bearer token,
                // the auth cookie rides every negotiate/poll/send request via the generic Headers bag.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookieHeader;
            })
            .Build();

    [Fact]
    public async Task Cookie_authenticated_client_receives_DashboardUpdated_on_acknowledge()
    {
        var activationId = await ArrangeActiveActivationAsync();
        var cookie = await LoginAndGetAuthCookieAsync(ManagerUserName);

        var received = new TaskCompletionSource<DashboardDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = BuildCookieConnection(cookie);
        connection.On<DashboardDto>("DashboardUpdated", dto => received.TrySetResult(dto));

        try
        {
            await connection.StartAsync();
            await connection.InvokeAsync("JoinActivation", activationId);

            await AcknowledgeAsync(activationId, _memberId);

            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(received.Task, "the cookie-authenticated connection should receive a DashboardUpdated push within 10s");

            var dto = await received.Task;
            dto.ActivationId.Should().Be(activationId);
            dto.ReadyCount.Should().Be(1, "the acknowledging member is now Ready");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
