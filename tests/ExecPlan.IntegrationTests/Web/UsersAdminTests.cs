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
/// Task 6: users administration (Index/Create/Edit) — Admin can create and edit users (deactivate =
/// <c>IsActive=false</c>, never delete), Manager gets a read-only list and is rejected from any write
/// endpoint. <see cref="TestAppFactory"/> only pre-seeds its own "admin" account, so this class seeds a
/// Manager user + an Organization directly into the shared SQLite database, idempotently, the same
/// pattern <c>AuthFlowTests.EnsureRoleUsersSeeded</c> uses.
/// </summary>
[Collection("WebHostSequential")]
public class UsersAdminTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "users-admin-manager";
    private const string ManagerPassword = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;

    public UsersAdminTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "UsersAdmin Test Org");
        if (org is null)
        {
            org = new Organization { Name = "UsersAdmin Test Org" };
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
                FullName = "Users Admin Test Manager",
                Phone = "+96500000301",
                Role = UserRole.PlanManager,
                OrganizationId = _orgId,
                IsActive = true,
            });
            ctx.SaveChanges();
        }
    }

    [Fact]
    public async Task Admin_creates_user_persisted_and_hashed()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        const string newUserName = "created-by-admin-t1";
        const string plainPassword = "Passw0rd!";

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/users/create", "/admin/users/create",
            new Dictionary<string, string>
            {
                ["UserName"] = newUserName,
                ["Password"] = plainPassword,
                ["FullName"] = "Created By Admin",
                ["Phone"] = "+96500000401",
                ["Role"] = nameof(UserRole.PlanManager),
                ["OrganizationId"] = _orgId.ToString(),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/users");

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var created = await uow.Repo<User>().FirstOrDefaultAsync(u => u.UserName == newUserName);
        created.Should().NotBeNull();
        created!.PasswordHash.Should().NotBe(plainPassword);
        hasher.Verify(created.PasswordHash, plainPassword).Should().BeTrue();
        created.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Manager_sees_readonly_list_no_add_link()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var res = await client.GetAsync("/admin/users");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("/admin/users/create");
    }

    [Fact]
    public async Task Manager_cannot_post_create()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        const string blockedUserName = "manager-should-not-create-this";

        // Manager is Admin-gated OFF /admin/users/create (GET), so we scrape the antiforgery
        // token/cookie pair from the read-only /admin/users list the manager CAN see instead — per
        // WebTestHelpers, a valid token/cookie pair is accepted by any [ValidateAntiForgeryToken]
        // action in the app, not just the one that rendered the form.
        var res = await WebTestHelpers.PostFormAsync(client, "/admin/users", "/admin/users/create",
            new Dictionary<string, string>
            {
                ["UserName"] = blockedUserName,
                ["Password"] = "Passw0rd!",
                ["Role"] = nameof(UserRole.PlanManager),
                ["OrganizationId"] = _orgId.ToString(),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/denied");

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var found = await uow.Repo<User>().FirstOrDefaultAsync(u => u.UserName == blockedUserName);
        found.Should().BeNull();
    }

    [Fact]
    public async Task Deactivate_sets_isactive_false()
    {
        Guid targetId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var target = new User
            {
                UserName = "to-be-deactivated",
                PasswordHash = hasher.Hash("Passw0rd!"),
                FullName = "To Be Deactivated",
                Phone = "+96500000402",
                Role = UserRole.TeamLeader,
                OrganizationId = _orgId,
                IsActive = true,
            };
            ctx.Users.Add(target);
            ctx.SaveChanges();
            targetId = target.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

        var editUrl = $"/admin/users/{targetId}/edit";
        var res = await WebTestHelpers.PostFormAsync(client, editUrl, editUrl,
            new Dictionary<string, string>
            {
                ["UserName"] = "to-be-deactivated",
                ["FullName"] = "To Be Deactivated",
                ["Phone"] = "+96500000402",
                ["Role"] = nameof(UserRole.TeamLeader),
                ["OrganizationId"] = _orgId.ToString(),
                // Mirrors what an unchecked HTML checkbox actually posts on this form: the Edit view
                // renders a hidden name="IsActive" value="false" fallback right after the checkbox
                // (SimpleTypeModelBinder binds a bool from the FIRST value of a same-name field, so a
                // real browser posting only the hidden field — checkbox unchecked — binds to false).
                ["IsActive"] = "false",
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be("/admin/users");

        using var verifyScope = _factory.Services.CreateScope();
        var uow = verifyScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var reloaded = await uow.Repo<User>().FirstOrDefaultAsync(u => u.Id == targetId);
        reloaded.Should().NotBeNull();
        reloaded!.IsActive.Should().BeFalse();
    }
}
