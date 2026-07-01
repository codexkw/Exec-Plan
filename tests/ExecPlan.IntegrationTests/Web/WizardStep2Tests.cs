using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 10: create-plan wizard step 2 (teams &amp; members). <see cref="TestAppFactory"/> only pre-seeds
/// its own "admin" account, so this class seeds a Manager (owns the draft under test), a SECOND manager
/// (for the foreign-draft ownership test), and a couple of plain <see cref="User"/> rows to use as team
/// members/leaders — all idempotently, the same pattern <c>WizardStep1Tests</c>/<c>PlansListDetailTests</c>
/// use. Each test seeds its own <see cref="Plan"/> Draft directly (not by driving Step 1) so tests don't
/// interfere with each other.
/// </summary>
public class WizardStep2Tests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "wizard-step2-manager";
    private const string Manager2UserName = "wizard-step2-manager2";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _manager2Id;
    private Guid _member1Id;
    private Guid _member2Id;

    public WizardStep2Tests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WizardStep2 Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WizardStep2 Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Wizard Step2 Test Manager", "+96500000601", UserRole.PlanManager);
        _manager2Id = EnsureUser(ctx, hasher, Manager2UserName, "Wizard Step2 Test Manager 2", "+96500000602", UserRole.PlanManager);
        _member1Id = EnsureUser(ctx, hasher, "wizard-step2-member1", "Wizard Step2 Test Member 1", "+96500000603", UserRole.TeamMember);
        _member2Id = EnsureUser(ctx, hasher, "wizard-step2-member2", "Wizard Step2 Test Member 2", "+96500000604", UserRole.TeamMember);
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

    private Guid SeedDraft(string name, Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var plan = new Plan
        {
            Name = name,
            Type = PlanType.Daily,
            Status = PlanStatus.Draft,
            CreatedByUserId = ownerId,
        };
        ctx.Plans.Add(plan);
        ctx.SaveChanges();
        return plan.Id;
    }

    [Fact]
    public async Task Add_team_with_members_persists()
    {
        var planId = SeedDraft("WizardStep2 Add Team Plan", _managerId);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/teams";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("intent", "add"),
            new("Name", "Alpha Team"),
            new("TeamLeaderUserId", _member1Id.ToString()),
            new("MemberUserIds", _member1Id.ToString()),
            new("MemberUserIds", _member2Id.ToString()),
        };

        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl, fields);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var team = ctx.Teams.FirstOrDefault(t => t.PlanId == planId && t.Name == "Alpha Team");
        team.Should().NotBeNull();
        team!.TeamLeaderUserId.Should().Be(_member1Id);

        var memberIds = ctx.TeamMemberships.Where(m => m.TeamId == team.Id).Select(m => m.UserId).ToList();
        memberIds.Should().BeEquivalentTo(new[] { _member1Id, _member2Id });

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Alpha Team");
    }

    [Fact]
    public async Task Advancing_without_team_is_blocked()
    {
        var planId = SeedDraft("WizardStep2 Blocked Plan", _managerId);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/teams";
        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl,
            new Dictionary<string, string> { ["intent"] = "next" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var plan = ctx.Plans.First(p => p.Id == planId);
        plan.Status.Should().Be(PlanStatus.Draft);
    }

    [Fact]
    public async Task Foreign_draft_forbidden()
    {
        var planId = SeedDraft("WizardStep2 Foreign Plan", _managerId);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, Manager2UserName, Password);

        var res = await client.GetAsync($"/admin/plans/create/{planId}/teams");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/denied");
    }
}
