using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
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
}
