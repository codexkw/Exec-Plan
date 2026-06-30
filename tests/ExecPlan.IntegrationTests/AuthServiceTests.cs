using ExecPlan.Application.Auth;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Auth;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ExecPlan.IntegrationTests;

public class AuthServiceTests : IClassFixture<SqliteFixture>
{
    private const string Password = "Cor!rectHorse9";

    private readonly SqliteFixture _fx;

    public AuthServiceTests(SqliteFixture fx) => _fx = fx;

    private static IConfiguration BuildJwtConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "execplan-tests",
                ["Jwt:Audience"] = "execplan-tests",
                ["Jwt:SigningKey"] = "unit-test-signing-key-needs-32-chars-minimum-0123456789",
                ["Jwt:AccessTokenMinutes"] = "30",
            })
            .Build();

    // Each call gets a fresh ExecPlanDbContext/UnitOfWork (sharing the fixture's single Sqlite
    // in-memory connection, per SqliteFixture's contract) and a fresh, uniquely-named seeded user
    // so tests sharing the IClassFixture don't collide on the unique UserName index.
    private (AuthService Service, User User, TestClock Clock) CreateSut()
    {
        var ctx = _fx.NewContext();
        var uow = new UnitOfWork(ctx);
        var hasher = new IdentityPasswordHasher();

        var user = new User
        {
            UserName = $"user-{Guid.NewGuid():N}",
            FullName = "Test User",
            Phone = "+96500000000",
            Role = UserRole.TeamLeader,
            OrganizationId = Guid.NewGuid(),
            IsActive = true,
            PasswordHash = hasher.Hash(Password),
        };
        ctx.Set<User>().Add(user);
        ctx.SaveChanges();

        var clock = new TestClock();
        var jwtFactory = new JwtTokenFactory(BuildJwtConfig());
        var refreshTokenStore = new RefreshTokenStore(uow, clock);
        var service = new AuthService(uow, hasher, jwtFactory, refreshTokenStore, clock);

        return (service, user, clock);
    }

    [Fact]
    public async Task Login_with_valid_credentials_returns_tokens()
    {
        var (service, user, _) = CreateSut();

        var pair = await service.LoginAsync(user.UserName, Password);

        pair.AccessToken.Should().NotBeNullOrWhiteSpace();
        pair.RefreshToken.Should().NotBeNullOrWhiteSpace();
        pair.UserId.Should().Be(user.Id);
        pair.Role.Should().Be(UserRole.TeamLeader);
        pair.FullName.Should().Be(user.FullName);
    }

    [Fact]
    public async Task Login_with_wrong_password_throws_Unauthorized()
    {
        var (service, user, _) = CreateSut();

        var act = async () => await service.LoginAsync(user.UserName, "definitely-wrong-password");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Unauthorized);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_revokes_old()
    {
        var (service, user, clock) = CreateSut();
        var first = await service.LoginAsync(user.UserName, Password);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        var second = await service.RefreshAsync(first.RefreshToken);

        second.RefreshToken.Should().NotBe(first.RefreshToken);
        second.AccessToken.Should().NotBeNullOrWhiteSpace();
        second.UserId.Should().Be(user.Id);

        // The old refresh token was rotated out — reusing it must now fail.
        var reuseOld = async () => await service.RefreshAsync(first.RefreshToken);
        var thrown = await reuseOld.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Unauthorized);

        // The new refresh token, however, is still usable.
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var third = await service.RefreshAsync(second.RefreshToken);
        third.RefreshToken.Should().NotBe(second.RefreshToken);
    }

    [Fact]
    public async Task Refresh_with_revoked_or_unknown_token_throws()
    {
        var (service, _, _) = CreateSut();

        var act = async () => await service.RefreshAsync("not-a-real-refresh-token-value");

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_expired_token_throws()
    {
        var (service, user, clock) = CreateSut();
        var first = await service.LoginAsync(user.UserName, Password);

        // Default RefreshTokenDays is 14; jump well past expiry.
        clock.UtcNow = clock.UtcNow.AddDays(15);

        var act = async () => await service.RefreshAsync(first.RefreshToken);

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Unauthorized);
    }
}
