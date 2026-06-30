using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Domain.Enums;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// End-to-end proof of the Task-17 CRUD controllers' authorization matrix (PRD §14): Admin-only writes
/// with Manager+Admin read access on Organizations/Departments/Users, vs Manager+Admin read+write on
/// Plans/Teams/etc. Also covers a couple of full round-trips (create -> read-back) and the
/// PasswordHash-never-leaks guarantee on the Users endpoints.
/// </summary>
public class CrudAndVisibilityTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public CrudAndVisibilityTests(TestAppFactory factory) => _factory = factory;

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record UserDto(Guid Id, string UserName, string FullName, string Phone, UserRole Role, Guid OrganizationId, Guid? DepartmentId, bool IsActive);

    private sealed record OrganizationDto(Guid Id, string Name);

    private sealed record DepartmentDto(Guid Id, string Name, Guid OrganizationId);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    private Task<HttpClient> AdminClientAsync() => LoginAsAsync(TestAppFactory.AdminUserName, TestAppFactory.AdminPassword);

    /// <summary>Creates a user with the given role via the admin-only Users API, then logs in as them.
    /// This is the "create via admin API" approach the brief calls out for getting manager/member tokens.</summary>
    private async Task<HttpClient> CreateAndLoginAsAsync(HttpClient adminClient, UserRole role, string suffix)
    {
        var userName = $"{role}-{suffix}";
        const string password = "Test-Pass-w0rd-123";

        var createResponse = await adminClient.PostAsJsonAsync("/api/v1/users", new
        {
            userName,
            password,
            fullName = $"{role} Test User",
            phone = "+96500000001",
            role,
            organizationId = _factory.AdminOrganizationId,
            departmentId = (Guid?)null,
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        return await LoginAsAsync(userName, password);
    }

    [Fact]
    public async Task Unauthenticated_get_users_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_can_create_user_and_read_it_back_without_password_hash()
    {
        var admin = await AdminClientAsync();

        var createResponse = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            userName = "round-trip-user",
            password = "Some-Pass-w0rd-123",
            fullName = "Round Trip User",
            phone = "+96500000002",
            role = UserRole.TeamMember,
            organizationId = _factory.AdminOrganizationId,
            departmentId = (Guid?)null,
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createdBody = await createResponse.Content.ReadAsStringAsync();
        createdBody.Should().NotContain("PasswordHash");
        createdBody.Should().NotContain("passwordHash");

        var created = await createResponse.Content.ReadFromJsonAsync<UserDto>();
        created.Should().NotBeNull();
        created!.UserName.Should().Be("round-trip-user");

        var getResponse = await admin.GetAsync($"/api/v1/users/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getBody = await getResponse.Content.ReadAsStringAsync();
        getBody.Should().NotContain("PasswordHash");
        getBody.Should().NotContain("passwordHash");

        var fetched = await getResponse.Content.ReadFromJsonAsync<UserDto>();
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.Role.Should().Be(UserRole.TeamMember);
    }

    [Fact]
    public async Task Admin_can_create_organization_and_department_round_trip()
    {
        var admin = await AdminClientAsync();

        var orgResponse = await admin.PostAsJsonAsync("/api/v1/organizations", new { name = "Round Trip Org" });
        orgResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var org = await orgResponse.Content.ReadFromJsonAsync<OrganizationDto>();
        org.Should().NotBeNull();

        var getOrgResponse = await admin.GetAsync($"/api/v1/organizations/{org!.Id}");
        getOrgResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetchedOrg = await getOrgResponse.Content.ReadFromJsonAsync<OrganizationDto>();
        fetchedOrg!.Name.Should().Be("Round Trip Org");

        var deptResponse = await admin.PostAsJsonAsync("/api/v1/departments", new { name = "Round Trip Dept", organizationId = org.Id });
        deptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var dept = await deptResponse.Content.ReadFromJsonAsync<DepartmentDto>();
        dept.Should().NotBeNull();
        dept!.OrganizationId.Should().Be(org.Id);

        var getDeptResponse = await admin.GetAsync($"/api/v1/departments/{dept.Id}");
        getDeptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fetchedDept = await getDeptResponse.Content.ReadFromJsonAsync<DepartmentDto>();
        fetchedDept!.Name.Should().Be("Round Trip Dept");
    }

    [Fact]
    public async Task Manager_can_read_users_and_create_plans_but_cannot_create_users()
    {
        var admin = await AdminClientAsync();
        var manager = await CreateAndLoginAsAsync(admin, UserRole.PlanManager, "mgr-1");

        var listResponse = await manager.GetAsync("/api/v1/users");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createUserResponse = await manager.PostAsJsonAsync("/api/v1/users", new
        {
            userName = "manager-cannot-create",
            password = "Some-Pass-w0rd-123",
            fullName = "Should Not Be Created",
            phone = "+96500000003",
            role = UserRole.TeamMember,
            organizationId = _factory.AdminOrganizationId,
            departmentId = (Guid?)null,
        });
        createUserResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var createPlanResponse = await manager.PostAsJsonAsync("/api/v1/plans", new
        {
            name = "Manager Authored Plan",
            type = PlanType.Daily,
            objective = "Objective",
            description = "Description",
            scope = "Scope",
            contacts = (object?)null,
            activators = (object?)null,
        });
        createPlanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Member_cannot_read_users()
    {
        var admin = await AdminClientAsync();
        var member = await CreateAndLoginAsAsync(admin, UserRole.TeamMember, "mem-1");

        var listResponse = await member.GetAsync("/api/v1/users");

        listResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
