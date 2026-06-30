using ExecPlan.Application.Common;
using ExecPlan.Application.Execution;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using ExecPlan.Infrastructure.Realtime;
using FluentAssertions;

namespace ExecPlan.IntegrationTests;

public class AcknowledgeTests : IClassFixture<SqliteFixture>
{
    private readonly SqliteFixture _fx;
    public AcknowledgeTests(SqliteFixture fx) => _fx = fx;

    private static readonly DateTime NowUtc = new(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc);

    private AcknowledgeService NewService()
    {
        var uow = new UnitOfWork(_fx.NewContext());
        return new AcknowledgeService(uow, new TestClock { UtcNow = NowUtc }, new NoOpRealtimeNotifier());
    }

    // Seeds a minimal activation with one participant; returns (activationId, participantUserId).
    private (Guid ActivationId, Guid UserId) SeedActivationWithParticipant()
    {
        using var ctx = _fx.NewContext();

        var activation = new PlanActivation
        {
            PlanId = Guid.NewGuid(),
            Status = ActivationStatus.Active,
            Shift = ShiftBand.Morning,
            RosterDate = new DateTime(2026, 6, 30),
            ActivatedByUserId = Guid.NewGuid(),
            ActivatedAtUtc = NowUtc,
            EscalationThreshold = 5,
        };
        ctx.Add(activation);

        var participant = new ActivationParticipant
        {
            ActivationId = activation.Id,
            UserId = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            TeamNameSnapshot = "Alpha",
            Status = ParticipantStatus.Pending,
        };
        ctx.Add(participant);
        ctx.SaveChanges();

        return (activation.Id, participant.UserId);
    }

    [Fact]
    public async Task Acknowledge_sets_ready_and_writes_response()
    {
        var (activationId, userId) = SeedActivationWithParticipant();

        await NewService().AcknowledgeAsync(activationId, userId);

        await using var ctx = _fx.NewContext();
        var participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == userId);
        participant.Status.Should().Be(ParticipantStatus.Ready);

        var responses = ctx.Set<ResponseStatus>()
            .Where(r => r.ActivationId == activationId && r.ParticipantId == participant.Id).ToList();
        responses.Should().HaveCount(1);
        responses[0].AcknowledgedAtUtc.Should().Be(NowUtc);
    }

    [Fact]
    public async Task Acknowledge_is_idempotent()
    {
        var (activationId, userId) = SeedActivationWithParticipant();

        var service = NewService();
        await service.AcknowledgeAsync(activationId, userId);
        await service.AcknowledgeAsync(activationId, userId);

        await using var ctx = _fx.NewContext();
        var participant = ctx.Set<ActivationParticipant>()
            .Single(p => p.ActivationId == activationId && p.UserId == userId);
        participant.Status.Should().Be(ParticipantStatus.Ready);

        ctx.Set<ResponseStatus>()
            .Count(r => r.ActivationId == activationId && r.ParticipantId == participant.Id)
            .Should().Be(1);
    }

    [Fact]
    public async Task Acknowledge_by_non_participant_throws_Forbidden()
    {
        var (activationId, _) = SeedActivationWithParticipant();

        var act = async () => await NewService().AcknowledgeAsync(activationId, Guid.NewGuid());

        var thrown = await act.Should().ThrowAsync<AppException>();
        thrown.Which.ErrorKind.Should().Be(AppException.Kind.Forbidden);
    }
}
