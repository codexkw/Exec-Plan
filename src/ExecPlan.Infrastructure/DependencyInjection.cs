using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Infrastructure.Auth;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration cfg)
    {
        var provider = cfg["Database:Provider"] ?? "SqlServer";
        s.AddDbContext<ExecPlanDbContext>(o =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                o.UseSqlite(cfg.GetConnectionString("Default"));
            }
            else
            {
                o.UseSqlServer(cfg.GetConnectionString("Default"));
            }
        });

        s.AddScoped<IUnitOfWork, UnitOfWork>();
        s.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        s.AddSingleton<IClock, KuwaitClock>();

        s.AddScoped<IPasswordHasher, IdentityPasswordHasher>();
        s.AddScoped<IJwtTokenFactory, JwtTokenFactory>();
        s.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        s.AddScoped<IAuthService>(sp =>
        {
            var refreshTokenDays = int.TryParse(cfg["Jwt:RefreshTokenDays"], out var days) ? days : 14;
            return new AuthService(
                sp.GetRequiredService<IUnitOfWork>(),
                sp.GetRequiredService<IPasswordHasher>(),
                sp.GetRequiredService<IJwtTokenFactory>(),
                sp.GetRequiredService<IRefreshTokenStore>(),
                sp.GetRequiredService<IClock>(),
                refreshTokenDays);
        });

        // INotificationProvider/DatabasePlaceholderProvider are registered in a later task (Task 10)
        // once those types exist.
        return s;
    }
}
