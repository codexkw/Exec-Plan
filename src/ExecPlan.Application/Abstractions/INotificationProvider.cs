using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Abstractions;

public interface INotificationProvider
{
    // Stages rows into the UoW; does NOT save. Returns the created log/attempt entities.
    NotificationLog StageNotification(Guid activationId, Guid recipientUserId, NotificationKind kind, string body, DateTime utcNow);
    CallAttempt StageCallAttempt(Guid activationId, Guid participantId, int attemptNumber, DateTime utcNow);
}
