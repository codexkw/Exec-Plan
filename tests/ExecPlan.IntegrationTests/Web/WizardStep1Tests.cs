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
/// Task 9: create-plan wizard step 1 (plan info -> Draft). <see cref="TestAppFactory"/> only pre-seeds
/// its own "admin" account, so this class seeds a Manager + Organization directly into the shared
/// SQLite database, idempotently, the same pattern <c>UsersAdminTests</c>/<c>PlansListDetailTests</c> use.
/// </summary>
[Collection("WebHostSequential")]
public class WizardStep1Tests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "wizard-step1-manager";
    private const string ManagerPassword = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _managerId;

    public WizardStep1Tests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WizardStep1 Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WizardStep1 Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        var manager = ctx.Users.FirstOrDefault(u => u.UserName == ManagerUserName);
        if (manager is null)
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            manager = new User
            {
                UserName = ManagerUserName,
                PasswordHash = hasher.Hash(ManagerPassword),
                FullName = "Wizard Step1 Test Manager",
                Phone = "+96500000501",
                Role = UserRole.PlanManager,
                OrganizationId = org.Id,
                IsActive = true,
            };
            ctx.Users.Add(manager);
            ctx.SaveChanges();
        }

        _managerId = manager.Id;
    }

    [Fact]
    public async Task Post_info_creates_draft_and_redirects_to_teams()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/plans/create", "/admin/plans/create",
            new Dictionary<string, string>
            {
                ["Name"] = "P1",
                ["Type"] = nameof(PlanType.Emergency),
            });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = res.Headers.Location!.ToString();
        location.Should().MatchRegex(@"^/admin/plans/create/[0-9a-fA-F-]+/teams$");

        var planId = Guid.Parse(location.Split('/')[4]);

        using var scope = _factory.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var plan = await uow.Repo<Plan>().FirstOrDefaultAsync(p => p.Id == planId);
        plan.Should().NotBeNull();
        plan!.Status.Should().Be(PlanStatus.Draft);
        plan.CreatedByUserId.Should().Be(_managerId);
        plan.Name.Should().Be("P1");
        plan.Type.Should().Be(PlanType.Emergency);

        var activator = await uow.Repo<PlanActivator>()
            .FirstOrDefaultAsync(a => a.PlanId == planId && a.UserId == _managerId);
        activator.Should().NotBeNull();
    }

    [Fact]
    public async Task Missing_name_re_renders_step1()
    {
        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, ManagerPassword);

        var res = await WebTestHelpers.PostFormAsync(client, "/admin/plans/create", "/admin/plans/create",
            new Dictionary<string, string>
            {
                ["Type"] = nameof(PlanType.Daily),
            });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("admin/plans/create");
    }
}
