using System.Net;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 13: the Activate button on Plan Detail (<c>PlansController.Activate</c>, <c>POST
/// /admin/plans/{id}/activate</c>) plus the oversized-launch confirm region in <c>Detail.cshtml</c>.
/// <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class seeds two
/// PlanManager users + an Organization directly into the shared SQLite database, idempotently (same
/// pattern as <c>PlansListDetailTests</c>/<c>WizardStep4Tests</c>): <see cref="ManagerUserName"/> is the
/// plan's <c>CreatedByUserId</c> (an implicitly authorized activator per <c>ActivationService</c>'s
/// creator check), <see cref="OtherManagerUserName"/> is a second manager who is neither the creator
/// nor a registered <see cref="PlanActivator"/>, used for the Forbidden scenario. The happy-path roster
/// is seeded aligned EXACTLY to <see cref="TestAppFactory.FixedShift"/> (Band + RosterDate) with
/// <c>SubstituteForUserId=null</c> — <c>ActivationService.ActivateAsync</c> only counts a
/// <see cref="ShiftAssignment"/> as on-duty when all three match the host's fixed clock's resolved
/// shift; a Draft plan with no roster at all reproduces the service's common "no one on duty" Conflict.
/// </summary>
[Collection("WebHostSequential")]
public class ActivateTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "activate-manager";
    private const string OtherManagerUserName = "activate-other-manager";
    private const string MemberUserName = "activate-member";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _otherManagerId;
    private Guid _memberId;

    public ActivateTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "Activate Test Org");
        if (org is null)
        {
            org = new Organization { Name = "Activate Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Activate Test Manager", "+96500000901", UserRole.PlanManager);
        _otherManagerId = EnsureUser(ctx, hasher, OtherManagerUserName, "Activate Test Other Manager", "+96500000902", UserRole.PlanManager);
        _memberId = EnsureUser(ctx, hasher, MemberUserName, "Activate Test Member", "+96500000903", UserRole.TeamMember);
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
    public async Task Activate_ready_plan_redirects_to_dashboard()
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

            var plan = new Plan
            {
                Name = "Activate Ready Plan",
                Type = PlanType.Daily,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();

            var team = new Team { PlanId = plan.Id, Name = "Activate Team" };
            ctx.Teams.Add(team);
            ctx.SaveChanges();

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

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/{planId}";

        var detail = await client.GetAsync(getUrl);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailBody = await detail.Content.ReadAsStringAsync();
        detailBody.Should().Contain("btn-launch"); // the oversized amber launch button

        var res = await WebTestHelpers.PostFormAsync(client, getUrl, $"{getUrl}/activate", new Dictionary<string, string>());

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        location.Should().MatchRegex(@"^/admin/activations/[0-9a-fA-F-]{36}$");

        var activationId = Guid.Parse(location.Split('/').Last());

        using var verifyScope = _factory.Services.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var activation = verifyCtx.PlanActivations.FirstOrDefault(a => a.Id == activationId);
        activation.Should().NotBeNull();
        activation!.PlanId.Should().Be(planId);
        activation.Status.Should().Be(ActivationStatus.Active);
    }

    [Fact]
    public async Task Activate_unauthorized_activator_denied()
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var plan = new Plan
            {
                Name = "Activate Forbidden Plan",
                Type = PlanType.Daily,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();
            planId = plan.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, OtherManagerUserName, Password);

        var getUrl = $"/admin/plans/{planId}";

        // Neither the plan's creator nor a registered PlanActivator -> any OTHER manager can still
        // reach the class-gated Detail page (class policy is ManagerOrAdmin, no ownership check there)...
        var detail = await client.GetAsync(getUrl);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);

        // ...but the service itself rejects the activation attempt with Forbidden.
        var res = await WebTestHelpers.PostFormAsync(client, getUrl, $"{getUrl}/activate", new Dictionary<string, string>());

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/denied");
    }

    [Fact]
    public async Task Activate_draft_plan_shows_conflict()
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var plan = new Plan
            {
                Name = "Activate Draft Plan",
                Type = PlanType.Daily,
                Status = PlanStatus.Draft,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();
            planId = plan.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/{planId}";

        var res = await WebTestHelpers.PostFormAsync(client, getUrl, $"{getUrl}/activate", new Dictionary<string, string>());

        // Draft plan with no roster at all -> the service's authorization/already-active checks both
        // pass (creator, no existing activation) but on-duty resolution finds nothing -> Conflict,
        // caught by the controller and redirected back to Detail with a TempData message.
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be(getUrl);

        var follow = await client.GetAsync(getUrl);
        follow.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await follow.Content.ReadAsStringAsync();
        body.Should().Contain("alert-danger");
        // The Arabic admin renders the LOCALIZED (ar) AppError.NoOneOnDuty message, never the raw English
        // literal thrown from ActivationService (MUST-FIX 4).
        body.Should().Contain(ResxValue("AppError.NoOneOnDuty"));
        body.Should().NotContain("No one is on duty for this shift.");
    }

    /// <summary>Reads the ar value of a resx key so the assertion tracks the real localized string.</summary>
    private static string ResxValue(string key, [CallerFilePath] string here = "")
    {
        var webDir = Path.GetDirectoryName(here)!; // .../tests/ExecPlan.IntegrationTests/Web
        var repoRoot = Path.GetFullPath(Path.Combine(webDir, "..", "..", ".."));
        var resxPath = Path.Combine(repoRoot, "src", "ExecPlan.Api", "Resources", "SharedResource.ar.resx");
        var doc = XDocument.Load(resxPath);
        return doc.Root!.Elements("data")
            .First(e => (string)e.Attribute("name")! == key)
            .Element("value")!.Value;
    }
}
