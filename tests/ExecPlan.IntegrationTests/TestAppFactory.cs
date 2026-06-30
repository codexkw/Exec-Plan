using System.Data.Common;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Auth;
using ExecPlan.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Hosts the real <c>ExecPlan.Api</c> (<see cref="Program"/>) over a single shared SQLite
/// <c>:memory:</c> connection kept open for the factory's lifetime, so every DI scope the host
/// creates talks to the same in-memory database (mirrors <see cref="SqliteFixture"/>'s pattern, but
/// for a full <see cref="WebApplicationFactory{TEntryPoint}"/> host instead of a bare DbContext).
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    public const string AdminUserName = "admin";
    public const string AdminPassword = "Adm!n-Pass-w0rd-123";

    public Guid AdminUserId { get; private set; }
    public Guid AdminOrganizationId { get; } = Guid.NewGuid();

    // Deterministic host clock: a fixed instant that resolves to a known Kuwait shift, so the
    // activation cycle (which calls KuwaitShiftCalculator.Resolve(IClock.UtcNow)) always rosters
    // against the same (Band, RosterDate) — tests seed ShiftAssignments aligned to FixedShift below.
    // 2026-06-30 08:00 UTC = 11:00 Asia/Kuwait → Morning band, roster 2026-06-30. Replaces the
    // default KuwaitClock in the test host only. JWT expiry uses real DateTime.UtcNow in
    // JwtTokenFactory (not IClock), so fixing this never invalidates freshly issued tokens.
    private readonly TestClock _clock = new();

    /// <summary>The fixed instant the host's <see cref="IClock"/> reports (UTC).</summary>
    public DateTime FixedUtcNow => _clock.UtcNow;

    /// <summary>The Kuwait shift the fixed clock resolves to — seed rosters against this.</summary>
    public ShiftResolution FixedShift => new KuwaitShiftCalculator().Resolve(_clock.UtcNow);

    private readonly DbConnection _connection;

    // Both Program.cs's AddInfrastructure(builder.Configuration) call AND its JWT-bearer-options setup
    // read straight from builder.Configuration synchronously, at Program.cs build time — i.e. BEFORE
    // WebApplicationFactory gets a chance to splice in ConfigureWebHost's ConfigureAppConfiguration
    // overrides (those are only applied at the builder.Build() call boundary, deep inside Program.cs,
    // by which point AddInfrastructure/the JWT options have already read the ORIGINAL appsettings.json
    // values). Environment variables, however, ARE visible to the default config sources
    // WebApplication.CreateBuilder(args) loads from the very start, so they are the only override
    // mechanism that reaches those early/eager reads. Without this: (a) AddInfrastructure would wire
    // UseSqlServer before our later DbContext swap, tripping EF's "only a single database provider can
    // be registered" check; (b) the JWT bearer's TokenValidationParameters would validate against the
    // ORIGINAL signing key while JwtTokenFactory (which reads config lazily, per-request, well after
    // the host has fully started) signs with the override key — a silent signature mismatch that shows
    // up only as a 401 on an otherwise-valid token.
    private static readonly Dictionary<string, string?> ConfigOverrides = new()
    {
        ["Database:Provider"] = "Sqlite",
        ["Jwt:Issuer"] = "execplan-tests",
        ["Jwt:Audience"] = "execplan-tests",
        ["Jwt:SigningKey"] = "integration-test-signing-key-needs-32-chars-minimum-0123456789",
        ["Jwt:AccessTokenMinutes"] = "30",
        ["Jwt:RefreshTokenDays"] = "14",
    };

    // Same keys as ConfigOverrides, pre-translated to the env-var form actually set below, so the
    // constructor and Dispose's cleanup can never drift out of sync with each other.
    private static readonly string[] EnvVarKeys = ConfigOverrides.Keys
        .Select(k => k.Replace(':', '_').Replace("_", "__"))
        .ToArray();

    public TestAppFactory()
    {
        var values = ConfigOverrides.Values.ToArray();
        for (var i = 0; i < EnvVarKeys.Length; i++)
        {
            Environment.SetEnvironmentVariable(EnvVarKeys[i], values[i]);
        }

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(ConfigOverrides);
        });

        builder.ConfigureServices(services =>
        {
            // Replace whatever provider AddInfrastructure(config) wired up with one bound to the
            // single shared open connection above, so all scopes see the same in-memory DB.
            services.RemoveAll<DbContextOptions<ExecPlanDbContext>>();
            services.RemoveAll<ExecPlanDbContext>();

            services.AddDbContext<ExecPlanDbContext>(o => o.UseSqlite(_connection));

            // Replace the default singleton KuwaitClock with the fixed test clock so shift
            // resolution is deterministic across the whole activation cycle (Escalation:DefaultThreshold
            // is left untouched at its default 5 — the DI test asserts that value).
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(_clock);

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            ctx.Database.EnsureCreated();

            SeedAdmin(ctx);
        });
    }

    private void SeedAdmin(ExecPlanDbContext ctx)
    {
        var hasher = new IdentityPasswordHasher();
        var admin = new User
        {
            UserName = AdminUserName,
            PasswordHash = hasher.Hash(AdminPassword),
            FullName = "Integration Test Admin",
            Phone = "+96500000000",
            Role = UserRole.SystemAdmin,
            OrganizationId = AdminOrganizationId,
            IsActive = true,
        };

        ctx.Users.Add(admin);
        ctx.SaveChanges();

        AdminUserId = admin.Id;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();

            // Undo the process-global env vars set in the constructor so a later
            // WebApplicationFactory fixture in the same test run doesn't inherit them.
            foreach (var envVarKey in EnvVarKeys)
            {
                Environment.SetEnvironmentVariable(envVarKey, null);
            }
        }
    }
}
