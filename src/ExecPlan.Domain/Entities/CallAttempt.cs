using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class CallAttempt : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid ParticipantId { get; set; }
    public int AttemptNumber { get; set; }
}
