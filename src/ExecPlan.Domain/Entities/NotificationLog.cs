using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class NotificationLog : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid RecipientUserId { get; set; }
    public NotificationKind Kind { get; set; }
    public string Body { get; set; } = "";
}
