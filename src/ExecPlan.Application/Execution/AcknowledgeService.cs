using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Execution;

/// <summary>
/// The single counted response: «أنا جاهز». The readiness tap is the ONLY signal that marks a
/// participant as having responded (design §invariant — opening/viewing/completing tasks never
/// counts). Writes exactly one <see cref="ResponseStatus"/> per participant and flips their status to
/// <see cref="ParticipantStatus.Ready"/>; calling it again is a no-op (idempotent).
/// </summary>
public interface IAcknowledgeService
{
    Task AcknowledgeAsync(Guid activationId, Guid actingUserId, CancellationToken ct = default);
}

/// <inheritdoc cref="IAcknowledgeService"/>
public sealed class AcknowledgeService : IAcknowledgeService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly IRealtimeNotifier _realtime;

    public AcknowledgeService(IUnitOfWork uow, IClock clock, IRealtimeNotifier realtime)
    {
        _uow = uow;
        _clock = clock;
        _realtime = realtime;
    }

    public async Task AcknowledgeAsync(Guid activationId, Guid actingUserId, CancellationToken ct = default)
    {
        // Only an actual participant of this activation may acknowledge.
        var participant = await _uow.Repo<ActivationParticipant>()
            .FirstOrDefaultAsync(p => p.ActivationId == activationId && p.UserId == actingUserId, ct);
        if (participant is null)
        {
            throw AppException.Forbidden("You are not a participant in this activation.");
        }

        // Idempotent: a second tap finds the existing response and changes nothing.
        var existing = await _uow.Repo<ResponseStatus>()
            .FirstOrDefaultAsync(
                rs => rs.ActivationId == activationId && rs.ParticipantId == participant.Id, ct);
        if (existing is not null)
        {
            return;
        }

        var response = new ResponseStatus
        {
            ActivationId = activationId,
            ParticipantId = participant.Id,
            AcknowledgedAtUtc = _clock.UtcNow,
        };
        await _uow.Repo<ResponseStatus>().AddAsync(response, ct);

        // Re-load the participant TRACKED (the predicate read above is no-tracking) so the status
        // flip is actually persisted by SaveChanges.
        var tracked = await _uow.Repo<ActivationParticipant>().GetByIdAsync(participant.Id, ct);
        tracked!.Status = ParticipantStatus.Ready;

        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activationId, ct);
    }
}
