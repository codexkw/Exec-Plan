using System.Net;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Task 7: departments + organizations administration (Index/Create) — Admin can create both,
/// Manager gets a read-only list for each and is rejected from either write endpoint.
/// <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class seeds a Manager
/// user directly into the shared SQLite database, idempotently, the same pattern
/// <c>AuthFlowTests.EnsureRoleUsersSeeded</c> / <c>UsersAdminTests.EnsureSeeded</c> use.
/// </summary>
public class OrgDeptAdminTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "orgdept-admin-manager";
    private const string ManagerPassword = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;

    public OrgDeptAdminTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "OrgDeptAdmin Test Org");
        if (org is null)
        {
            org = new Organization { Name = "OrgDeptAdmin Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;

        if (!ctx.Users.Any(u => u.UserName == ManagerUserName))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            ctx.Users.Add(new User
            {
                UserName = ManagerUserName,
                PasswordHash = hasher.Hash(ManagerPassword),
                FullName = "OrgDept Admin Test Manager",
                Phone = "+96500000501",
                Role = UserRole.PlanManager,
                OrganizationId = _orgId,
                IsActive = true,
            });
            ctx.SaveChanges();
        }
    }

    [Fact]
    public async Task Admin_creates_organization()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        const string newOrgName = "Created Org T7";

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/organizations/create", "/admin/organizations/create",
            new Dictionary<string, string> { ["Name"] = newOrgName });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/organizations");

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var created = await uow.Repo<Organization>().FirstOrDefaultAsync(o => o.Name == newOrgName);
        created.Should().NotBeNull();
    }

    [Fact]
    public async Task Admin_creates_department_under_org()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        const string newDeptName = "Created Dept T7";

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/departments/create", "/admin/departments/create",
            new Dictionary<string, string>
            {
                ["Name"] = newDeptName,
                ["OrganizationId"] = _orgId.ToString(),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/departments");

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var created = await uow.Repo<Department>().FirstOrDefaultAsync(d => d.Name == newDeptName);
        created.Should().NotBeNull();
        created!.OrganizationId.Should().Be(_orgId);
    }

    [Fact]
    public async Task Manager_reads_but_cannot_write_dept()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var indexRes = await client.GetAsync("/admin/departments");
        indexRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await indexRes.Content.ReadAsStringAsync();
        body.Should().NotContain("/admin/departments/create");

        const string blockedDeptName = "manager-should-not-create-this-dept";

        // Manager is Admin-gated OFF /admin/departments/create (GET), so scrape the antiforgery
        // token/cookie pair from the read-only /admin/departments list the manager CAN see instead —
        // per WebTestHelpers, a valid token/cookie pair is accepted by any
        // [ValidateAntiForgeryToken] action in the app, not just the one that rendered it.
        var res = await WebTestHelpers.PostFormAsync(client, "/admin/departments", "/admin/departments/create",
            new Dictionary<string, string>
            {
                ["Name"] = blockedDeptName,
                ["OrganizationId"] = _orgId.ToString(),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/denied");

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var found = await uow.Repo<Department>().FirstOrDefaultAsync(d => d.Name == blockedDeptName);
        found.Should().BeNull();
    }
}
