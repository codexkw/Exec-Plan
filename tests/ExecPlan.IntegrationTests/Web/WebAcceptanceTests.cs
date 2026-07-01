using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 17 — the PRD §21 "Web" acceptance row, driven entirely through the MVC admin area in-process
/// (never <c>/api/v1</c>): a Plan Manager signs in under the default <c>ar</c> culture, walks the
/// 4-step create-plan wizard to a <see cref="PlanStatus.Ready"/> plan, activates it, and watches the
/// live dashboard render real Arabic labels and counters. <see cref="TestAppFactory"/> only pre-seeds
/// its own "admin" account, so this class seeds a PlanManager + Organization + one TeamMember directly
/// into the shared SQLite database, idempotently — the same pattern every other <c>Web</c> test class
/// uses (see <c>WizardStep4Tests</c>/<c>DashboardTests</c>).
///
/// THE key trap this test must get right: <c>PlanWizardController.BuildReviewVmAsync</c> pre-fills each
/// roster row's GET default with <c>Shift = ShiftBand.Morning</c> and <c>Date = DateTime.UtcNow.Date</c>
/// — the REAL wall clock, not the host's fixed <see cref="TestAppFactory.FixedShift"/> the activation
/// path resolves on-duty rows against. Posting those defaults verbatim would make
/// <c>ActivationService.ActivateAsync</c> find zero on-duty rows and throw a Conflict ("no one on duty"),
/// which the controller redirects back to Detail rather than to the dashboard — silently breaking this
/// exact end-to-end flow. So the Finish POST below overrides <c>roster[0].Shift</c>/<c>roster[0].Date</c>
/// to <see cref="TestAppFactory.FixedShift"/>'s <c>Band</c>/<c>RosterDate</c> instead of trusting the
/// pre-filled defaults, exactly as <c>WizardStep4Tests</c>/<c>ActivateTests</c> already do.
/// </summary>
[Collection("WebHostSequential")]
public class WebAcceptanceTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "webaccept-manager";
    private const string MemberUserName = "webaccept-member1";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _memberId;

    public WebAcceptanceTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WebAcceptance Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WebAcceptance Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "WebAcceptance Test Manager", "+96500009001", UserRole.PlanManager);
        _memberId = EnsureUser(ctx, hasher, MemberUserName, "WebAcceptance Test Member", "+96500009002", UserRole.TeamMember);
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

    [Fact]
    public async Task Manager_creates_activates_and_watches_dashboard_in_arabic()
    {
        // A lightweight "proof this is all in-process" record of every URL this test requests. The
        // whole flow only ever hits /admin/* + / paths on the in-process TestServer; none of it goes to
        // the REST /api/v1 surface, unlike AcceptanceFlowTests (the Phase 1 API-driven §21 counterpart).
        var requestedUrls = new List<string>();

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);
        requestedUrls.Add("/admin/login");

        // ---- Wizard step 1: plan info -> Draft -----------------------------------------------------
        var step1Res = await WebTestHelpers.PostFormAsync(client, "/admin/plans/create", "/admin/plans/create",
            new Dictionary<string, string>
            {
                ["Name"] = "Web Acceptance Plan",
                ["Type"] = nameof(PlanType.Daily),
                ["Objective"] = "Prove the whole manager flow works end to end in Arabic",
            });
        requestedUrls.Add("/admin/plans/create");

        step1Res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var step1Location = step1Res.Headers.Location!.ToString();
        step1Location.Should().MatchRegex(@"^/admin/plans/create/[0-9a-fA-F-]+/teams$");
        var planId = Guid.Parse(step1Location.Split('/')[4]);

        // ---- Wizard step 2: add a team with one member, then advance -------------------------------
        var teamsUrl = $"/admin/plans/create/{planId}/teams";
        var addTeamRes = await WebTestHelpers.PostFormAsync(client, teamsUrl, teamsUrl,
            new List<KeyValuePair<string, string>>
            {
                new("intent", "add"),
                new("Name", "Field Team Alpha"),
                new("MemberUserIds", _memberId.ToString()),
            });
        requestedUrls.Add(teamsUrl);
        addTeamRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var team = ctx.Teams.First(t => t.PlanId == planId && t.Name == "Field Team Alpha");
            _teamId = team.Id;
        }

        var nextTeamsRes = await WebTestHelpers.PostFormAsync(client, teamsUrl, teamsUrl,
            new Dictionary<string, string> { ["intent"] = "next" });
        requestedUrls.Add(teamsUrl);
        nextTeamsRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        nextTeamsRes.Headers.Location!.ToString().Should().Be($"/admin/plans/create/{planId}/tasks");

        // ---- Wizard step 3: add one task to the team, then advance ---------------------------------
        var tasksUrl = $"/admin/plans/create/{planId}/tasks";
        var addTaskRes = await WebTestHelpers.PostFormAsync(client, tasksUrl, tasksUrl,
            new Dictionary<string, string>
            {
                ["intent"] = "add",
                ["TeamId"] = _teamId.ToString(),
                ["Title"] = "Inspect the site",
                ["Order"] = "1",
                ["DurationMinutes"] = "45",
            });
        requestedUrls.Add(tasksUrl);
        addTaskRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var nextTasksRes = await WebTestHelpers.PostFormAsync(client, tasksUrl, tasksUrl,
            new Dictionary<string, string> { ["intent"] = "next" });
        requestedUrls.Add(tasksUrl);
        nextTasksRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        nextTasksRes.Headers.Location!.ToString().Should().Be($"/admin/plans/create/{planId}/review");

        // ---- Wizard step 4: roster + Finish -> Ready -----------------------------------------------
        // KEY TRAP: the Review GET pre-fills Shift=Morning / Date=DateTime.UtcNow.Date (real wall
        // clock). Activation resolves on-duty rows against the host's FIXED IClock instead
        // (TestAppFactory.FixedShift), so posting those defaults verbatim would leave zero on-duty
        // rows and Activate would fail with Conflict "no one is on duty". Align the roster to the
        // fixed shift explicitly.
        var reviewUrl = $"/admin/plans/create/{planId}/review";
        var fixedShift = _factory.FixedShift;
        var finishRes = await WebTestHelpers.PostFormAsync(client, reviewUrl, reviewUrl,
            new List<KeyValuePair<string, string>>
            {
                new("roster[0].TeamId", _teamId.ToString()),
                new("roster[0].UserId", _memberId.ToString()),
                new("roster[0].Shift", fixedShift.Band.ToString()),
                new("roster[0].Date", fixedShift.RosterDate.ToString("yyyy-MM-dd")),
            });
        requestedUrls.Add(reviewUrl);

        finishRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        finishRes.Headers.Location!.ToString().Should().Be($"/admin/plans/{planId}");

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            ctx.Plans.First(p => p.Id == planId).Status.Should().Be(PlanStatus.Ready);
        }

        // ---- Plan Detail: real Arabic launch label + RTL --------------------------------------------
        var detailUrl = $"/admin/plans/{planId}";
        var detailRes = await client.GetAsync(detailUrl);
        requestedUrls.Add(detailUrl);
        detailRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailBody = await detailRes.Content.ReadAsStringAsync();

        detailBody.Should().Contain("dir=\"rtl\"");

        var activateLabel = GetArabicString("Plans.Activate");
        detailBody.Should().Contain(activateLabel);
        detailBody.Should().Contain("btn-launch");

        // ---- Activate -> redirect to the live dashboard ---------------------------------------------
        var activateRes = await WebTestHelpers.PostFormAsync(client, detailUrl, $"{detailUrl}/activate",
            new Dictionary<string, string>());
        requestedUrls.Add($"{detailUrl}/activate");

        activateRes.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var activateLocation = activateRes.Headers.Location!.ToString();
        activateLocation.Should().MatchRegex(@"^/admin/activations/[0-9a-fA-F-]{36}$");
        var activationId = Guid.Parse(activateLocation.Split('/').Last());

        // ---- Live dashboard: RTL + the five real Arabic counter labels + rendered numbers -----------
        var dashboardUrl = $"/admin/activations/{activationId}";
        var dashboardRes = await client.GetAsync(dashboardUrl);
        requestedUrls.Add(dashboardUrl);
        dashboardRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var dashboardBody = await dashboardRes.Content.ReadAsStringAsync();

        dashboardBody.Should().Contain("dir=\"rtl\"");
        dashboardBody.Should().Contain(GetArabicString("Dash.Total"));
        dashboardBody.Should().Contain(GetArabicString("Dash.Pending"));
        dashboardBody.Should().Contain(GetArabicString("Dash.Ready"));
        dashboardBody.Should().Contain(GetArabicString("Dash.Escalated"));
        dashboardBody.Should().Contain(GetArabicString("Dash.Inducted"));

        // One participant (the single roster row), untouched -> Pending=1, everything else 0.
        dashboardBody.Should().Contain("data-counter=\"total\" data-value=\"1\"");
        dashboardBody.Should().Contain("data-counter=\"pending\" data-value=\"1\"");
        dashboardBody.Should().Contain("data-counter=\"ready\" data-value=\"0\"");
        dashboardBody.Should().Contain("data-counter=\"escalated\" data-value=\"0\"");
        dashboardBody.Should().Contain("data-counter=\"inducted\" data-value=\"0\"");

        // ---- Proof the whole flow stayed in-process: never touched the REST /api surface ------------
        requestedUrls.Should().NotBeEmpty();
        requestedUrls.Should().OnlyContain(url => !url.StartsWith("/api", StringComparison.Ordinal));
    }

    private Guid _teamId;

    /// <summary>
    /// Reads the live Arabic value of a resource key straight out of the running host's
    /// <see cref="IStringLocalizer{SharedResource}"/> (forced to the <c>ar</c> culture) rather than
    /// hard-coding a string the test author typed by hand — so this assertion tracks the real
    /// <c>.ar.resx</c> content and fails loudly if a key is ever renamed or its Arabic text changes.
    /// </summary>
    private string GetArabicString(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizerFactory>();
        var localizer = factory.Create(typeof(ExecPlan.Api.Resources.SharedResource));

        var previousCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ar");
            return localizer[key].Value;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previousCulture;
        }
    }
}
