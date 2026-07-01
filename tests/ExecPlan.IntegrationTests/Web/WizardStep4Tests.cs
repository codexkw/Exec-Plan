using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 12: create-plan wizard step 4 (shifts &amp; review -&gt; Ready) + the <c>RequireStep</c>
/// navigation guard. <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class
/// seeds a Manager + Organization + two team members directly into the shared SQLite database,
/// idempotently, the same pattern <c>WizardStep2Tests</c>/<c>WizardStep3Tests</c> use.
/// <see cref="SeedReadyForReviewDraft"/> builds a fully-prepared Draft (team + 2 members + 1 task) that
/// satisfies every wizard prerequisite, so Finish tests can post a valid roster straight away;
/// <see cref="SeedBareDraftNoTasks"/> builds one that has a team+member (satisfies step 3) but no task
/// (fails step 4's prerequisite), for the deep-link redirect test.
/// </summary>
public class WizardStep4Tests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "wizard-step4-manager";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _member1Id;
    private Guid _member2Id;

    public WizardStep4Tests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WizardStep4 Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WizardStep4 Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Wizard Step4 Test Manager", "+96500000801", UserRole.PlanManager);
        _member1Id = EnsureUser(ctx, hasher, "wizard-step4-member1", "Wizard Step4 Test Member 1", "+96500000802", UserRole.TeamMember);
        _member2Id = EnsureUser(ctx, hasher, "wizard-step4-member2", "Wizard Step4 Test Member 2", "+96500000803", UserRole.TeamMember);
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

    private (Guid PlanId, Guid TeamId) SeedReadyForReviewDraft(string planName, string teamName)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var plan = new Plan
        {
            Name = planName,
            Type = PlanType.Daily,
            Status = PlanStatus.Draft,
            CreatedByUserId = _managerId,
        };
        ctx.Plans.Add(plan);
        ctx.SaveChanges();

        var team = new Team { PlanId = plan.Id, Name = teamName };
        ctx.Teams.Add(team);
        ctx.SaveChanges();

        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member1Id });
        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member2Id });
        ctx.SaveChanges();

        ctx.TaskTemplates.Add(new TaskTemplate { TeamId = team.Id, Title = "Close affected roads", Order = 1, Duration = TimeSpan.FromMinutes(30) });
        ctx.SaveChanges();

        return (plan.Id, team.Id);
    }

    private Guid SeedBareDraftNoTasks(string planName, string teamName)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var plan = new Plan
        {
            Name = planName,
            Type = PlanType.Daily,
            Status = PlanStatus.Draft,
            CreatedByUserId = _managerId,
        };
        ctx.Plans.Add(plan);
        ctx.SaveChanges();

        var team = new Team { PlanId = plan.Id, Name = teamName };
        ctx.Teams.Add(team);
        ctx.SaveChanges();

        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member1Id });
        ctx.SaveChanges();

        return plan.Id;
    }

    [Fact]
    public async Task Finish_sets_plan_ready_and_persists_roster()
    {
        var (planId, teamId) = SeedReadyForReviewDraft("WizardStep4 Finish Plan", "Alpha Team");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/review";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("roster[0].TeamId", teamId.ToString()),
            new("roster[0].UserId", _member1Id.ToString()),
            new("roster[0].Shift", nameof(ShiftBand.Morning)),
            new("roster[0].Date", "2026-07-01"),
            new("roster[1].TeamId", teamId.ToString()),
            new("roster[1].UserId", _member2Id.ToString()),
            new("roster[1].Shift", nameof(ShiftBand.Evening)),
            new("roster[1].Date", "2026-07-01"),
            new("roster[1].SubstituteForUserId", _member1Id.ToString()),
        };

        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl, fields);

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be($"/admin/plans/{planId}");

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var assignments = ctx.ShiftAssignments.Where(a => a.TeamId == teamId).ToList();
        assignments.Should().HaveCount(2);
        assignments.Should().Contain(a => a.UserId == _member1Id && a.Shift == ShiftBand.Morning && a.SubstituteForUserId == null);
        assignments.Should().Contain(a => a.UserId == _member2Id && a.Shift == ShiftBand.Evening && a.SubstituteForUserId == _member1Id);

        var plan = ctx.Plans.First(p => p.Id == planId);
        plan.Status.Should().Be(PlanStatus.Ready);
    }

    [Fact]
    public async Task Empty_roster_blocks_finish()
    {
        var (planId, teamId) = SeedReadyForReviewDraft("WizardStep4 Empty Roster Plan", "Beta Team");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/review";
        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl, new Dictionary<string, string>());

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var plan = ctx.Plans.First(p => p.Id == planId);
        plan.Status.Should().Be(PlanStatus.Draft);
        ctx.ShiftAssignments.Where(a => a.TeamId == teamId).Should().BeEmpty();
    }

    [Fact]
    public async Task Deeplink_to_review_before_prereqs_redirects_back()
    {
        var planId = SeedBareDraftNoTasks("WizardStep4 Deeplink Plan", "Gamma Team");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var res = await client.GetAsync($"/admin/plans/create/{planId}/review");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be($"/admin/plans/create/{planId}/tasks");
    }
}
