using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class PersistenceTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public PersistenceTests(SqliteFixture fx) => _fx = fx;

    [Fact]
    public async Task Can_persist_and_read_back_a_plan()
    {
        // BaseEntity.Id is ctor-assigned with a protected setter (Domain convention #2), so the
        // round-trip id comes from the constructed entity rather than an object-initializer override.
        var plan = new Plan { Name = "P", Type = PlanType.Guard, CreatedByUserId = Guid.NewGuid() };
        var planId = plan.Id;
        await using (var ctx = _fx.NewContext())
        {
            ctx.Set<Plan>().Add(plan);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _fx.NewContext())
        {
            (await ctx.Set<Plan>().FindAsync(planId))!.Name.Should().Be("P");
        }
    }

    [Fact]
    public async Task SaveChanges_stamps_CreatedAtUtc_on_insert()
    {
        var plan = new Plan { Name = "Stamped", Type = PlanType.Daily, CreatedByUserId = Guid.NewGuid() };
        var planId = plan.Id;
        await using (var ctx = _fx.NewContext())
        {
            ctx.Set<Plan>().Add(plan);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _fx.NewContext())
        {
            var saved = await ctx.Set<Plan>().FindAsync(planId);
            saved!.CreatedAtUtc.Should().NotBe(default);
        }
    }

    [Fact]
    public void SaveChanges_sync_stamps_CreatedAtUtc_on_insert()
    {
        // Covers the sync SaveChanges(bool) funnel override added alongside the async one, so
        // CreatedAtUtc stamping behaves identically whether callers save sync or async.
        var plan = new Plan { Name = "StampedSync", Type = PlanType.Daily, CreatedByUserId = Guid.NewGuid() };
        var planId = plan.Id;
        using (var ctx = _fx.NewContext())
        {
            ctx.Set<Plan>().Add(plan);
            ctx.SaveChanges();
        }
        using (var ctx = _fx.NewContext())
        {
            var saved = ctx.Set<Plan>().Find(planId);
            saved!.CreatedAtUtc.Should().NotBe(default);
        }
    }

    [Fact]
    public async Task Repository_FirstOrDefaultAsync_finds_seeded_row_by_predicate()
    {
        var plan = new Plan { Name = "Findable", Type = PlanType.Guard, CreatedByUserId = Guid.NewGuid() };
        await using var ctx = _fx.NewContext();
        ctx.Set<Plan>().Add(plan);
        await ctx.SaveChangesAsync();

        var repo = new Repository<Plan>(ctx);
        var found = await repo.FirstOrDefaultAsync(p => p.Name == "Findable");

        found.Should().NotBeNull();
        found!.Id.Should().Be(plan.Id);
    }

    [Fact]
    public async Task Repository_ListAsync_applies_predicate_and_returns_only_matches()
    {
        await using var ctx = _fx.NewContext();
        ctx.Set<Plan>().Add(new Plan { Name = "ListMatchA", Type = PlanType.Guard, CreatedByUserId = Guid.NewGuid() });
        ctx.Set<Plan>().Add(new Plan { Name = "ListMatchB", Type = PlanType.Guard, CreatedByUserId = Guid.NewGuid() });
        ctx.Set<Plan>().Add(new Plan { Name = "ListNoMatch", Type = PlanType.Daily, CreatedByUserId = Guid.NewGuid() });
        await ctx.SaveChangesAsync();

        var repo = new Repository<Plan>(ctx);
        var matches = await repo.ListAsync(p => p.Name.StartsWith("ListMatch"));

        matches.Should().HaveCount(2);
        matches.Select(p => p.Name).Should().BeEquivalentTo("ListMatchA", "ListMatchB");
    }
}
