using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Xunit;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Covers the two read-aggregate helpers the admin dashboard added to <c>IRepository&lt;T&gt;</c>:
/// <c>CountAsync</c> (server-side count, optional predicate) and <c>ListRecentAsync</c> (top-N by a key,
/// ordered + limited server-side). Each test scopes its assertions to its own marker
/// (<c>ActivatedByUserId</c>) so the two methods can share the one <see cref="SqliteFixture"/> DB without
/// cross-contaminating counts.
/// </summary>
public sealed class RepositoryReadTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public RepositoryReadTests(SqliteFixture fx) => _fx = fx;

    private static readonly DateTime Base = new(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc);

    private static PlanActivation Activation(Guid actor, DateTime at, ActivationStatus status) => new()
    {
        PlanId = Guid.NewGuid(),
        Status = status,
        Shift = ShiftBand.Morning,
        RosterDate = Base.Date,
        ActivatedByUserId = actor,
        ActivatedAtUtc = at,
        EscalationThreshold = 5,
    };

    [Fact]
    public async Task ListRecentAsync_returns_top_n_ordered_by_key_descending()
    {
        var actor = Guid.NewGuid();
        await using (var seed = _fx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                seed.Add(Activation(actor, Base.AddMinutes(i), ActivationStatus.Active));
            }
            await seed.SaveChangesAsync();
        }

        var uow = new UnitOfWork(_fx.NewContext());
        var recent = await uow.Repo<PlanActivation>()
            .ListRecentAsync(a => a.ActivatedAtUtc, 3, a => a.ActivatedByUserId == actor);

        recent.Should().HaveCount(3);
        recent.Select(a => a.ActivatedAtUtc).Should().BeInDescendingOrder();
        recent[0].ActivatedAtUtc.Should().Be(Base.AddMinutes(4)); // newest first
        recent[2].ActivatedAtUtc.Should().Be(Base.AddMinutes(2)); // 4,3,2 — the 3 newest
    }

    [Fact]
    public async Task CountAsync_counts_all_and_filtered()
    {
        var actor = Guid.NewGuid();
        await using (var seed = _fx.NewContext())
        {
            seed.Add(Activation(actor, Base, ActivationStatus.Active));
            seed.Add(Activation(actor, Base.AddMinutes(1), ActivationStatus.Active));
            seed.Add(Activation(actor, Base.AddMinutes(2), ActivationStatus.Closed));
            seed.Add(Activation(actor, Base.AddMinutes(3), ActivationStatus.Closed));
            await seed.SaveChangesAsync();
        }

        var repo = new UnitOfWork(_fx.NewContext()).Repo<PlanActivation>();

        (await repo.CountAsync(a => a.ActivatedByUserId == actor)).Should().Be(4);
        (await repo.CountAsync(a => a.ActivatedByUserId == actor && a.Status == ActivationStatus.Active))
            .Should().Be(2);
    }
}
