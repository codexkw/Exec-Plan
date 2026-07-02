using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Auth;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Phase 3 Part A / gap G4 — stable machine error codes on the wire. Clients localize on the CODE, never
/// the English message. This proves both surfaces now emit it: the middleware JSON body
/// (<c>{ error, kind, code }</c>) for any thrown <see cref="ExecPlan.Application.Common.AppException"/>,
/// and the auth 401 body (<c>{ message, code }</c>) for login/refresh. Codes come from the canonical
/// <see cref="ExecPlan.Application.Common.AppErrorCodes"/> catalogue.
/// </summary>
[Collection("WebHostSequential")]
public class ErrorCodeContractTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public ErrorCodeContractTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Errc-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    // Superset shape covering both error bodies: middleware {error,kind,code} and auth {message,code}.
    private sealed record ApiError(string? Error, string? Kind, string? Code, string? Message);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private User MakeUser(ExecPlanDbContext ctx, IdentityPasswordHasher hasher, Guid orgId, UserRole role, string label) =>
        new()
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            FullName = label,
            Phone = "+96500000000",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            OrganizationId = orgId,
            IsActive = true,
        };

    [Fact]
    public async Task Login_with_a_bad_password_returns_the_InvalidCredentials_code()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userName = TestAppFactory.AdminUserName, password = "definitely-wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<ApiError>();
        body!.Code.Should().Be("InvalidCredentials");
        body.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Activating_with_no_one_on_duty_returns_the_NoOneOnDuty_code()
    {
        string managerUserName;
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = new IdentityPasswordHasher();
            var org = new Organization { Name = $"Errc-{Guid.NewGuid():N}" };
            ctx.Organizations.Add(org);
            var manager = MakeUser(ctx, hasher, org.Id, UserRole.PlanManager, "errc-mgr");
            ctx.Users.Add(manager);
            var plan = new Plan { Name = "Errc Plan", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = manager.Id };
            ctx.Plans.Add(plan);
            ctx.Teams.Add(new Team { PlanId = plan.Id, Name = "T", TeamLeaderUserId = null }); // no shift assignment → no one on duty
            ctx.SaveChanges();
            managerUserName = manager.UserName;
            planId = plan.Id;
        }

        var client = await LoginAsAsync(managerUserName, Password);
        var response = await client.PostAsync($"/api/v1/plans/{planId}/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<ApiError>();
        body!.Kind.Should().Be("Conflict");
        body.Code.Should().Be("NoOneOnDuty");
    }

    [Fact]
    public async Task Activating_a_plan_you_do_not_own_returns_the_NotAuthorizedToActivate_code()
    {
        string otherManagerUserName;
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = new IdentityPasswordHasher();
            var org = new Organization { Name = $"Errc-{Guid.NewGuid():N}" };
            ctx.Organizations.Add(org);
            var creator = MakeUser(ctx, hasher, org.Id, UserRole.PlanManager, "errc-creator");
            var other = MakeUser(ctx, hasher, org.Id, UserRole.PlanManager, "errc-other");
            ctx.Users.AddRange(creator, other);
            var plan = new Plan { Name = "Owned Plan", Type = PlanType.Guard, Status = PlanStatus.Ready, CreatedByUserId = creator.Id };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();
            otherManagerUserName = other.UserName;
            planId = plan.Id;
        }

        var client = await LoginAsAsync(otherManagerUserName, Password);
        var response = await client.PostAsync($"/api/v1/plans/{planId}/activate", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<ApiError>();
        body!.Kind.Should().Be("Forbidden");
        body.Code.Should().Be("NotAuthorizedToActivate");
    }

    [Fact]
    public async Task A_manager_raising_an_issue_returns_the_RaiseIssueLeaderOnly_code()
    {
        string managerUserName;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var hasher = new IdentityPasswordHasher();
            var org = new Organization { Name = $"Errc-{Guid.NewGuid():N}" };
            ctx.Organizations.Add(org);
            var manager = MakeUser(ctx, hasher, org.Id, UserRole.PlanManager, "errc-issue-mgr");
            ctx.Users.Add(manager);
            ctx.SaveChanges();
            managerUserName = manager.UserName;
        }

        var client = await LoginAsAsync(managerUserName, Password);
        // The leader-only guard fires before the activation is even loaded, so any id triggers it.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/activations/{Guid.NewGuid()}/raise-issue", new { body = "smoke" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<ApiError>();
        body!.Code.Should().Be("RaiseIssueLeaderOnly");
    }
}
