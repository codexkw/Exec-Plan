using System.Net;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Execution;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 14: the live Dashboard (<c>Areas/Admin/Controllers/ActivationsController</c>) — server-rendered
/// <c>GET /admin/activations/{id}</c>, JSON snapshot <c>GET /admin/activations/{id}/snapshot</c>, and the
/// TeamLeader object-level "own teams" guard that must protect BOTH endpoints identically (a leader
/// cannot bypass the HTML gate by hitting the JSON snapshot directly). <see cref="TestAppFactory"/> only
/// pre-seeds its own "admin" account, so this class seeds its own Organization + PlanManager/2×
/// TeamLeader/2× TeamMember users idempotently (same pattern as <c>ActivateTests</c>). Every activation is
/// arranged by building a Ready <see cref="Plan"/> with team(s) whose <see cref="ShiftAssignment"/> rows
/// are aligned EXACTLY to <see cref="TestAppFactory.FixedShift"/> (Band + RosterDate,
/// <c>SubstituteForUserId=null</c>) then calling the real <see cref="IActivationService.ActivateAsync"/>
/// in a DI scope — same gotcha <c>ActivateTests</c> documents (misaligned roster → Conflict "no one on
/// duty"). Readiness/task-completion state needed for the rates/ranking assertions is applied directly
/// against the shared <see cref="ExecPlanDbContext"/> (task-done) or via the real
/// <see cref="AcknowledgeService"/> (readiness tap) rather than through <c>ExecutionService</c>, which
/// requires a request-scoped <see cref="ICurrentUser"/> that a bare DI scope does not provide.
/// </summary>
public class DashboardTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "dash-manager";
    private const string LeaderUserName = "dash-leader";
    private const string ForeignLeaderUserName = "dash-foreign-leader";
    private const string Member1UserName = "dash-member1";
    private const string Member2UserName = "dash-member2";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _leaderId;
    private Guid _foreignLeaderId;
    private Guid _member1Id;
    private Guid _member2Id;

    public DashboardTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "Dashboard Test Org");
        if (org is null)
        {
            org = new Organization { Name = "Dashboard Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Dashboard Test Manager", "+96500002001", UserRole.PlanManager);
        _leaderId = EnsureUser(ctx, hasher, LeaderUserName, "Dashboard Test Leader", "+96500002002", UserRole.TeamLeader);
        _foreignLeaderId = EnsureUser(ctx, hasher, ForeignLeaderUserName, "Dashboard Test Foreign Leader", "+96500002003", UserRole.TeamLeader);
        _member1Id = EnsureUser(ctx, hasher, Member1UserName, "Dashboard Test Member 1", "+96500002004", UserRole.TeamMember);
        _member2Id = EnsureUser(ctx, hasher, Member2UserName, "Dashboard Test Member 2", "+96500002005", UserRole.TeamMember);
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
    /// Seeds a fresh Ready <see cref="Plan"/> with one <see cref="Team"/> per <paramref name="teams"/>
    /// tuple (name, optional leader, member ids) — each team gets one <see cref="TaskTemplate"/> and an
    /// on-duty <see cref="ShiftAssignment"/> per member aligned to <see cref="TestAppFactory.FixedShift"/>
    /// — then activates it via the real <see cref="IActivationService"/>, returning the activation id.
    /// </summary>
    private async Task<Guid> ArrangeActiveActivationAsync(
        string planName, params (string TeamName, Guid? LeaderId, Guid[] MemberIds)[] teams)
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

            foreach (var (teamName, leaderId, memberIds) in teams)
            {
                var team = new Team { PlanId = plan.Id, Name = teamName, TeamLeaderUserId = leaderId };
                ctx.Teams.Add(team);
                ctx.SaveChanges();

                ctx.TaskTemplates.Add(new TaskTemplate
                {
                    TeamId = team.Id,
                    Title = teamName + " Task",
                    Order = 1,
                    Duration = TimeSpan.FromMinutes(30),
                });

                foreach (var memberId in memberIds)
                {
                    ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = memberId });
                    ctx.ShiftAssignments.Add(new ShiftAssignment
                    {
                        TeamId = team.Id,
                        UserId = memberId,
                        Shift = _factory.FixedShift.Band,
                        Date = _factory.FixedShift.RosterDate,
                        SubstituteForUserId = null,
                    });
                }

                ctx.SaveChanges();
            }

            planId = plan.Id;
        }

        using var activateScope = _factory.Services.CreateScope();
        var activation = activateScope.ServiceProvider.GetRequiredService<IActivationService>();
        return await activation.ActivateAsync(planId, _managerId, CancellationToken.None);
    }

    /// <summary>The real counted readiness tap (no <see cref="ICurrentUser"/> dependency — takes the acting user id directly).</summary>
    private async Task AcknowledgeAsync(Guid activationId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var ack = scope.ServiceProvider.GetRequiredService<AcknowledgeService>();
        await ack.AcknowledgeAsync(activationId, userId, CancellationToken.None);
    }

    /// <summary>Marks the given participant's (only) execution task Done directly against the DbContext.</summary>
    private void MarkTaskDone(Guid activationId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var participant = ctx.ActivationParticipants.First(p => p.ActivationId == activationId && p.UserId == userId);
        var task = ctx.ExecutionTasks.First(t => t.ParticipantId == participant.Id);
        task.Status = ExecTaskStatus.Done;
        task.CompletedAtUtc = DateTime.UtcNow;
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Dashboard_renders_five_counters_and_rates()
    {
        var activationId = await ArrangeActiveActivationAsync(
            "Dashboard Counters Plan",
            ("Team Alpha", _leaderId, new[] { _member1Id, _member2Id }));

        // member1 taps ready + completes their one task; member2 stays Pending/untouched:
        // Total=2, Pending=1, Ready=1, Escalated=0, Inducted=0; ResponseRate=1/2=50%;
        // TaskCompletionRate=1 done / 2 total tasks (one per participant) = 50%.
        await AcknowledgeAsync(activationId, _member1Id);
        MarkTaskDone(activationId, _member1Id);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync($"/admin/activations/{activationId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("الإجمالي");     // Dash.Total
        body.Should().Contain("قيد الانتظار"); // Dash.Pending
        body.Should().Contain("جاهز");         // Dash.Ready
        body.Should().Contain("تم التصعيد");   // Dash.Escalated
        body.Should().Contain("تم الإحلال");   // Dash.Inducted

        body.Should().Contain("data-counter=\"total\" data-value=\"2\"");
        body.Should().Contain("data-counter=\"pending\" data-value=\"1\"");
        body.Should().Contain("data-counter=\"ready\" data-value=\"1\"");
        body.Should().Contain("data-counter=\"escalated\" data-value=\"0\"");
        body.Should().Contain("data-counter=\"inducted\" data-value=\"0\"");

        body.Should().Contain("data-rate=\"response\">50%");
        body.Should().Contain("data-rate=\"task\">50%");
    }

    [Fact]
    public async Task Team_ranking_sorted_best_first()
    {
        // Names deliberately reverse-alphabetical vs. their expected score order, so the assertion can
        // only pass if the view really renders Score-descending (not an alphabetical accident): "Team
        // Zulu" gets its member acknowledged Ready (score 0.5) and must render ABOVE "Team Alpha", whose
        // member never responds (score 0) — the DashboardDto itself is already Score-sorted; the view
        // must NOT re-sort it.
        var activationId = await ArrangeActiveActivationAsync(
            "Dashboard Ranking Plan",
            ("Team Zulu", _leaderId, new[] { _member1Id }),
            ("Team Alpha", (Guid?)null, new[] { _member2Id }));

        await AcknowledgeAsync(activationId, _member1Id);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync($"/admin/activations/{activationId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        var zuluIndex = body.IndexOf("Team Zulu", StringComparison.Ordinal);
        var alphaIndex = body.IndexOf("Team Alpha", StringComparison.Ordinal);

        zuluIndex.Should().BeGreaterThan(-1);
        alphaIndex.Should().BeGreaterThan(-1);
        zuluIndex.Should().BeLessThan(alphaIndex, "the higher-scoring team must render first (best -> delayed)");
    }

    [Fact]
    public async Task Snapshot_returns_json_dto()
    {
        var activationId = await ArrangeActiveActivationAsync(
            "Dashboard Snapshot Plan",
            ("Team Snapshot", _leaderId, new[] { _member1Id }));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync($"/admin/activations/{activationId}/snapshot");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("totalParticipants");
    }

    [Fact]
    public async Task Leader_cannot_open_foreign_activation()
    {
        // The main activation's only participating team is led by _leaderId.
        var activationId = await ArrangeActiveActivationAsync(
            "Dashboard Foreign Leader Plan",
            ("Team Owned", _leaderId, new[] { _member1Id }));

        // _foreignLeaderId genuinely leads a team — just not one participating in THIS activation (it
        // sits on an entirely separate, never-activated plan), proving the guard checks participation in
        // this specific activation rather than just "does the caller lead anything at all".
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var foreignPlan = new Plan
            {
                Name = "Dashboard Foreign Plan",
                Type = PlanType.Daily,
                Status = PlanStatus.Draft,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(foreignPlan);
            ctx.SaveChanges();

            ctx.Teams.Add(new Team { PlanId = foreignPlan.Id, Name = "Team Foreign", TeamLeaderUserId = _foreignLeaderId });
            ctx.SaveChanges();
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ForeignLeaderUserName, Password);

        var dashboardRes = await client.GetAsync($"/admin/activations/{activationId}");
        dashboardRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        dashboardRes.Headers.Location!.ToString().Should().Be("/admin/denied");

        // The same guard must protect the JSON snapshot — a leader cannot bypass the HTML gate by
        // hitting it directly.
        var snapshotRes = await client.GetAsync($"/admin/activations/{activationId}/snapshot");
        snapshotRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        snapshotRes.Headers.Location!.ToString().Should().Be("/admin/denied");
    }

    [Fact]
    public async Task Member_cannot_reach_dashboard()
    {
        var activationId = await ArrangeActiveActivationAsync(
            "Dashboard Unauth Plan",
            ("Team Unauth", _leaderId, new[] { _member1Id }));

        // Unauthenticated: a TeamMember has no admin cookie at all (rejected at login, Task 3) — the
        // point here is simply that the endpoint requires auth in the first place.
        var client = WebTestHelpers.NewClient(_factory);

        var res = await client.GetAsync($"/admin/activations/{activationId}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/login");
    }
}
