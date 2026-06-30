using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Infrastructure.Notifications;

/// <summary>
/// Placeholder <see cref="INotificationProvider"/> that just persists a row recording the
/// notification/call attempt (no real SMS/voice/WhatsApp channel yet). Per the provider seam
/// (NFR-7), a real channel is a new <see cref="INotificationProvider"/> implementation + one DI
/// line — activation/escalation/broadcast services are unaffected.
///
/// Stages only: <see cref="IUnitOfWork.Repo{T}"/>.AddAsync adds the entity to the change tracker
/// but does NOT call SaveChangesAsync. The calling service owns the single transaction/SaveChanges
/// (atomicity, NFR-8).
/// </summary>
public sealed class DatabasePlaceholderProvider : INotificationProvider
{
    private readonly IUnitOfWork _uow;

    public DatabasePlaceholderProvider(IUnitOfWork uow) => _uow = uow;

    public NotificationLog StageNotification(Guid activationId, Guid recipientUserId, NotificationKind kind, string body, DateTime utcNow)
    {
        var log = new NotificationLog
        {
            ActivationId = activationId,
            RecipientUserId = recipientUserId,
            Kind = kind,
            Body = body,
            CreatedAtUtc = utcNow,
        };
        _uow.Repo<NotificationLog>().AddAsync(log).GetAwaiter().GetResult();
        return log;
    }

    public CallAttempt StageCallAttempt(Guid activationId, Guid participantId, int attemptNumber, DateTime utcNow)
    {
        var attempt = new CallAttempt
        {
            ActivationId = activationId,
            ParticipantId = participantId,
            AttemptNumber = attemptNumber,
            CreatedAtUtc = utcNow,
        };
        _uow.Repo<CallAttempt>().AddAsync(attempt).GetAwaiter().GetResult();
        return attempt;
    }
}
