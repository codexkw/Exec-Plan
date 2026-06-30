using System.Data.Common;
using ExecPlan.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ExecPlan.IntegrationTests;

public sealed class SqliteFixture : IDisposable
{
    public DbConnection Connection { get; }

    public SqliteFixture()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
        using var ctx = NewContext();
        ctx.Database.EnsureCreated();
    }

    public ExecPlanDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<ExecPlanDbContext>().UseSqlite(Connection).Options;
        return new ExecPlanDbContext(opts);
    }

    public void Dispose() => Connection.Dispose();
}
