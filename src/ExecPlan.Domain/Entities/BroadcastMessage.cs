using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class BroadcastMessage : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string Body { get; set; } = "";
}
