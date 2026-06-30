using ExecPlan.Application.Abstractions;
using ExecPlan.Infrastructure.Persistence;
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

        // IClock -> KuwaitClock, INotificationProvider/DatabasePlaceholderProvider, and auth
        // services are registered once those concrete types exist (Task 6, Task 8/9).
        return s;
    }
}
