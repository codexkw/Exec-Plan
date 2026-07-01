using System.Net;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 15: the dashboard action bar's real POST endpoints — run-escalation, broadcast, close — plus the
/// Activation Summary page (<c>GET /admin/activations/{id}/summary</c>), all on the existing
/// <c>Areas/Admin/Controllers/ActivationsController</c> (Task 14 built Dashboard/Snapshot/Index there).
/// Every new action is <c>ManagerOrAdmin</c> only (NOT TeamLeader) per this task's global constraints.
/// <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class seeds its own
/// Organization + PlanManager/TeamLeader/TeamMember users idempotently (same pattern as
/// <c>DashboardTests</c>/<c>ActivateTests</c>), and arranges an Active activation the same proven way:
/// a Ready <see cref="Plan"/> with one <see cref="Team"/>, a <see cref="TaskTemplate"/>, and a
/// non-substitute <see cref="ShiftAssignment"/> aligned to <see cref="TestAppFactory.FixedShift"/>, then
/// the real <see cref="IActivationService.ActivateAsync"/> in a DI scope.
///
/// <b>Leader-close 302-not-403 decision:</b> this app's cookie auth (<c>AdminCookie</c>) maps a role-policy
/// failure for an already-authenticated principal to <c>AccessDeniedPath</c> (302 <c>/admin/denied</c>),
/// never a bare 403 — <c>Leader_cannot_close</c> logs in as the seeded TeamLeader, scrapes a valid
/// antiforgery token+cookie from the dashboard GET (which the leader genuinely may view — they lead the
/// activation's only team), POSTs close with it, and asserts the 302 denied redirect plus that the
/// activation is still Active in a fresh scope (the close never happened).
/// </summary>
public class DashboardActionsTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "actdash-manager";
    private const string LeaderUserName = "actdash-leader";
    private const string MemberUserName = "actdash-member";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _leaderId;
    private Guid _memberId;

    public DashboardActionsTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "Dashboard Actions Test Org");
        if (org is null)
        {
            org = new Organization { Name = "Dashboard Actions Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Actions Test Manager", "+96500003001", UserRole.PlanManager);
        _leaderId = EnsureUser(ctx, hasher, LeaderUserName, "Actions Test Leader", "+96500003002", UserRole.TeamLeader);
        _memberId = EnsureUser(ctx, hasher, MemberUserName, "Actions Test Member", "+96500003003", UserRole.TeamMember);
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

    /// <summary>
    /// Seeds a fresh Ready <see cref="Plan"/> with one <see cref="Team"/> led by <see cref="_leaderId"/>
    /// containing <see cref="_memberId"/>, one <see cref="TaskTemplate"/>, and an on-duty
    /// <see cref="ShiftAssignment"/> aligned to <see cref="TestAppFactory.FixedShift"/> — then activates
    /// it via the real <see cref="IActivationService"/>, returning the activation id. Mirrors
    /// <c>DashboardTests.ArrangeActiveActivationAsync</c>/<c>ActivateTests</c>' proven arrangement.
    /// </summary>
    private async Task<Guid> ArrangeActiveActivationAsync(string planName)
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

            var plan = new Plan
            {
                Name = planName,
                Type = PlanType.Daily,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();

            var team = new Team { PlanId = plan.Id, Name = "Actions Team", TeamLeaderUserId = _leaderId };
            ctx.Teams.Add(team);
            ctx.SaveChanges();

            ctx.TaskTemplates.Add(new TaskTemplate
            {
                TeamId = team.Id,
                Title = "Actions Team Task",
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

    [Fact]
    public async Task Run_escalation_returns_to_dashboard()
    {
        var activationId = await ArrangeActiveActivationAsync("Actions Escalation Plan");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var dashUrl = $"/admin/activations/{activationId}";
        var res = await WebTestHelpers.PostFormAsync(client, dashUrl, $"{dashUrl}/run-escalation", new Dictionary<string, string>());

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be(dashUrl);
    }

    [Fact]
    public async Task Broadcast_persists_message()
    {
        var activationId = await ArrangeActiveActivationAsync("Actions Broadcast Plan");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var dashUrl = $"/admin/activations/{activationId}";
        var res = await WebTestHelpers.PostFormAsync(client, dashUrl, $"{dashUrl}/broadcast",
            new Dictionary<string, string> { ["body"] = "All hands: proceed to muster point." });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be(dashUrl);

        using var verifyScope = _factory.Services.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var message = ctx.BroadcastMessages.FirstOrDefault(m => m.ActivationId == activationId);
        message.Should().NotBeNull();
        message!.Body.Should().Be("All hands: proceed to muster point.");
    }

    [Fact]
    public async Task Close_redirects_to_summary_and_marks_closed()
    {
        var activationId = await ArrangeActiveActivationAsync("Actions Close Plan");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var dashUrl = $"/admin/activations/{activationId}";
        var summaryUrl = $"{dashUrl}/summary";

        var closeRes = await WebTestHelpers.PostFormAsync(client, dashUrl, $"{dashUrl}/close", new Dictionary<string, string>());
        closeRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        closeRes.Headers.Location!.ToString().Should().Be(summaryUrl);

        var summaryRes = await client.GetAsync(summaryUrl);
        summaryRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await summaryRes.Content.ReadAsStringAsync();

        // Action-bar-only marker: none of the three action routes/forms exist on the static Summary page.
        body.Should().NotContain("/run-escalation");
        body.Should().NotContain("/broadcast");
        body.Should().NotContain($"{dashUrl}/close");
        body.Should().NotContain("data-action-bar");

        // Final counters still render.
        body.Should().Contain("data-counter=\"total\" data-value=\"1\"");

        using var verifyScope = _factory.Services.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var activation = ctx.PlanActivations.First(a => a.Id == activationId);
        activation.Status.Should().Be(ActivationStatus.Closed);
    }

    [Fact]
    public async Task Summary_of_active_redirects_to_dashboard()
    {
        var activationId = await ArrangeActiveActivationAsync("Actions Summary Active Plan");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync($"/admin/activations/{activationId}/summary");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be($"/admin/activations/{activationId}");
    }

    [Fact]
    public async Task Leader_cannot_close()
    {
        // The leader genuinely leads the activation's only team, so they may view the dashboard (Task 14
        // behavior, unaffected by this task) — used here only to obtain a VALID antiforgery token+cookie,
        // so the POST close below is rejected specifically by the ManagerOrAdmin ROLE policy, not by a
        // missing/invalid antiforgery pair.
        var activationId = await ArrangeActiveActivationAsync("Actions Leader Close Plan");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, LeaderUserName, Password);

        var dashUrl = $"/admin/activations/{activationId}";
        var dashboardRes = await client.GetAsync(dashUrl);
        dashboardRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await WebTestHelpers.PostFormAsync(client, dashUrl, $"{dashUrl}/close", new Dictionary<string, string>());

        // Cookie authz failure for an authenticated-but-wrong-role principal -> 302 AccessDeniedPath,
        // never a bare 403 (this app's AdminCookie AccessDeniedPath=/admin/denied). The real Location is
        // an absolute URL plus a ReturnUrl query string (same shape UsersAdminTests/ActivateTests assert
        // against), so this checks Contain rather than an exact Be.
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/denied");

        using var verifyScope = _factory.Services.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var activation = ctx.PlanActivations.First(a => a.Id == activationId);
        activation.Status.Should().Be(ActivationStatus.Active);
    }
}
