using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 8: "My Plans" list (<c>Plans.Index</c>) + read-only plan detail (<c>Plans.Detail</c>).
/// <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class seeds a Manager
/// user + Organization directly into the shared SQLite database, idempotently (same pattern as
/// <c>UsersAdminTests</c>/<c>AuthFlowTests</c>); each test then arranges its own Plan/Team/TeamMembership/
/// TaskTemplate/PlanActivation rows so tests don't interfere with each other's list assertions.
/// </summary>
public class PlansListDetailTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "plans-list-manager";
    private const string ManagerPassword = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;

    public PlansListDetailTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "PlansListDetail Test Org");
        if (org is null)
        {
            org = new Organization { Name = "PlansListDetail Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        var manager = ctx.Users.FirstOrDefault(u => u.UserName == ManagerUserName);
        if (manager is null)
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            manager = new User
            {
                UserName = ManagerUserName,
                PasswordHash = hasher.Hash(ManagerPassword),
                FullName = "Plans List Test Manager",
                Phone = "+96500000501",
                Role = UserRole.PlanManager,
                OrganizationId = _orgId,
                IsActive = true,
            };
            ctx.Users.Add(manager);
            ctx.SaveChanges();
        }

        _managerId = manager.Id;
    }

    [Fact]
    public async Task Manager_sees_only_own_plans_with_badges()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var own = new Plan
            {
                Name = "Own Draft Plan T8",
                Type = PlanType.Daily,
                Status = PlanStatus.Draft,
                CreatedByUserId = _managerId,
            };
            var other = new Plan
            {
                Name = "Someone Elses Ready Plan T8",
                Type = PlanType.Weekly,
                Status = PlanStatus.Ready,
                CreatedByUserId = _factory.AdminUserId,
            };
            ctx.Plans.AddRange(own, other);
            ctx.SaveChanges();
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var res = await client.GetAsync("/admin/plans");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Own Draft Plan T8");
        body.Should().Contain("badge-draft");
        body.Should().NotContain("Someone Elses Ready Plan T8");
    }

    [Fact]
    public async Task Admin_sees_all_plans()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var managerPlan = new Plan
            {
                Name = "Admin Visible Manager Plan T8",
                Type = PlanType.Emergency,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            var adminPlan = new Plan
            {
                Name = "Admin Visible Admin Plan T8",
                Type = PlanType.General,
                Status = PlanStatus.Draft,
                CreatedByUserId = _factory.AdminUserId,
            };
            ctx.Plans.AddRange(managerPlan, adminPlan);
            ctx.SaveChanges();
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await client.GetAsync("/admin/plans");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Admin Visible Manager Plan T8");
        body.Should().Contain("Admin Visible Admin Plan T8");
        body.Should().Contain("badge-ready");
        body.Should().Contain("badge-draft");
    }

    [Fact]
    public async Task Detail_renders_teams_and_tasks()
    {
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

            var member = new User
            {
                UserName = "plans-list-detail-member",
                PasswordHash = hasher.Hash("Passw0rd!"),
                FullName = "Detail Test Member",
                Phone = "+96500000502",
                Role = UserRole.TeamMember,
                OrganizationId = _orgId,
                IsActive = true,
            };
            ctx.Users.Add(member);

            var plan = new Plan
            {
                Name = "Detail Test Plan T8",
                Type = PlanType.Guard,
                Status = PlanStatus.Ready,
                CreatedByUserId = _managerId,
            };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();

            var team = new Team { PlanId = plan.Id, Name = "Detail Test Team" };
            ctx.Teams.Add(team);
            ctx.SaveChanges();

            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = member.Id });
            ctx.TaskTemplates.Add(new TaskTemplate
            {
                TeamId = team.Id,
                Title = "Detail Test Task",
                Order = 1,
                Duration = TimeSpan.FromMinutes(30),
            });
            ctx.SaveChanges();

            planId = plan.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var res = await client.GetAsync($"/admin/plans/{planId}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Detail Test Plan T8");
        body.Should().Contain("Detail Test Team");
        body.Should().Contain("Detail Test Member");
        body.Should().Contain("Detail Test Task");
    }

    [Fact]
    public async Task Missing_plan_returns_404_redirect()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var res = await client.GetAsync($"/admin/plans/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/notfound");
    }
}
