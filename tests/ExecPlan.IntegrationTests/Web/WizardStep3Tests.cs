using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 11: create-plan wizard step 3 (tasks). <see cref="TestAppFactory"/> only pre-seeds its own
/// "admin" account, so this class seeds a Manager + Organization + a team member directly into the
/// shared SQLite database, idempotently, the same pattern <c>WizardStep1Tests</c>/<c>WizardStep2Tests</c>
/// use. Each test seeds its own <see cref="Plan"/> Draft with one <see cref="Team"/> (and a member)
/// directly (bypassing steps 1-2) so tests don't interfere with each other.
/// </summary>
public class WizardStep3Tests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "wizard-step3-manager";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _memberId;

    public WizardStep3Tests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WizardStep3 Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WizardStep3 Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Wizard Step3 Test Manager", "+96500000701", UserRole.PlanManager);
        _memberId = EnsureUser(ctx, hasher, "wizard-step3-member1", "Wizard Step3 Test Member", "+96500000702", UserRole.TeamMember);
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

    private (Guid PlanId, Guid TeamId) SeedDraftWithTeam(string planName, string teamName)
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

        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _memberId });
        ctx.SaveChanges();

        return (plan.Id, team.Id);
    }

    [Fact]
    public async Task Add_task_persists_with_duration()
    {
        var (planId, teamId) = SeedDraftWithTeam("WizardStep3 Add Task Plan", "Alpha Team");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/tasks";
        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl, new Dictionary<string, string>
        {
            ["intent"] = "add",
            ["TeamId"] = teamId.ToString(),
            ["Title"] = "Close affected roads",
            ["Order"] = "1",
            ["DurationMinutes"] = "30",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var task = ctx.TaskTemplates.FirstOrDefault(t => t.TeamId == teamId && t.Title == "Close affected roads");
        task.Should().NotBeNull();
        task!.Order.Should().Be(1);
        task.Duration.Should().Be(TimeSpan.FromMinutes(30));

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Close affected roads");
    }

    [Fact]
    public async Task Advancing_without_any_task_blocked()
    {
        var (planId, _) = SeedDraftWithTeam("WizardStep3 Blocked Plan", "Beta Team");

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var getUrl = $"/admin/plans/create/{planId}/tasks";
        var res = await WebTestHelpers.PostFormAsync(client, getUrl, getUrl,
            new Dictionary<string, string> { ["intent"] = "next" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var plan = ctx.Plans.First(p => p.Id == planId);
        plan.Status.Should().Be(PlanStatus.Draft);
    }
}
