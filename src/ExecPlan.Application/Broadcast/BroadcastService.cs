using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Broadcast;

/// <summary>
/// Manager/admin broadcast to every participant of an activation (design §5.6, FR-BRD-1). Writes one
/// <see cref="BroadcastMessage"/> and stages one <see cref="NotificationKind.Broadcast"/>
/// <see cref="NotificationLog"/> per participant via the provider seam (NFR-7), then commits everything
/// with a single <see cref="IUnitOfWork.SaveChangesAsync"/> (atomic, NFR-8). The realtime dashboard push
/// fires only after the commit succeeds. The acting sender is read from <see cref="ICurrentUser"/>.
/// </summary>
public sealed class BroadcastService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly ICurrentUser _cur;
    private readonly INotificationProvider _provider;
    private readonly IRealtimeNotifier _realtime;

    public BroadcastService(
        IUnitOfWork uow,
        IClock clock,
        ICurrentUser cur,
        INotificationProvider provider,
        IRealtimeNotifier realtime)
    {
        _uow = uow;
        _clock = clock;
        _cur = cur;
        _provider = provider;
        _realtime = realtime;
    }

    public async Task BroadcastAsync(Guid activationId, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw AppException.Validation("Broadcast body is required.", AppErrorCodes.BroadcastEmpty);
        }

        var isMgrAdmin = _cur.Role is UserRole.PlanManager or UserRole.SystemAdmin;
        if (!isMgrAdmin)
        {
            throw AppException.Forbidden("Only a manager or admin may broadcast.", AppErrorCodes.BroadcastManagerOnly);
        }

        if (_cur.UserId is null)
        {
            throw AppException.Forbidden("No authenticated sender for this broadcast.");
        }

        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(activationId, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        var participants = await _uow.Repo<ActivationParticipant>()
            .ListAsync(p => p.ActivationId == activationId, ct);

        var message = new BroadcastMessage
        {
            ActivationId = activationId,
            SenderUserId = _cur.UserId.Value,
            Body = body,
            CreatedAtUtc = _clock.UtcNow,
        };
        await _uow.Repo<BroadcastMessage>().AddAsync(message, ct);

        foreach (var p in participants)
        {
            _provider.StageNotification(
                activationId, p.UserId, NotificationKind.Broadcast, body, _clock.UtcNow);
        }

        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activationId, ct);
    }
}
