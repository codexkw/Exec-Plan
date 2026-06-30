using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class EscalationLog : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid SubstituteUserId { get; set; }
    public Guid NewParticipantId { get; set; }
}
