using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Domain.Enums;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// End-to-end proof of the auth pipeline wired in Program.cs: anonymous login/refresh, JWT bearer
/// validation, claim-type alignment with <c>JwtTokenFactory</c>, and the <c>[Authorize]</c>-protected
/// diagnostic endpoint.
/// </summary>
[Collection("WebHostSequential")]
public class AuthEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AuthEndpointTests(TestAppFactory factory) => _factory = factory;

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record WhoAmIDto(Guid? UserId, string? Role);

    [Fact]
    public async Task WhoAmI_without_token_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_then_whoami_with_bearer_token_returns_200_as_system_admin()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userName = TestAppFactory.AdminUserName,
            password = TestAppFactory.AdminPassword,
        });

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokenPairDto>();
        tokens.Should().NotBeNull();
        tokens!.AccessToken.Should().NotBeNullOrWhiteSpace();
        tokens.Role.Should().Be(UserRole.SystemAdmin);
        tokens.UserId.Should().Be(_factory.AdminUserId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var whoAmIResponse = await client.GetAsync("/api/v1/whoami");

        whoAmIResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var whoAmI = await whoAmIResponse.Content.ReadFromJsonAsync<WhoAmIDto>();
        whoAmI.Should().NotBeNull();
        whoAmI!.UserId.Should().Be(_factory.AdminUserId);
        whoAmI.Role.Should().Be(nameof(UserRole.SystemAdmin));
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userName = TestAppFactory.AdminUserName,
            password = "definitely-the-wrong-password",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
