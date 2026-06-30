using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ExecPlan.Infrastructure.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExecPlanDbContext>
{
    public ExecPlanDbContext CreateDbContext(string[] args)
    {
        var cfg = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var o = new DbContextOptionsBuilder<ExecPlanDbContext>()
            .UseSqlServer(cfg.GetConnectionString("Default")
                ?? "Server=.;Database=Exec-Plan;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new ExecPlanDbContext(o);
    }
}
