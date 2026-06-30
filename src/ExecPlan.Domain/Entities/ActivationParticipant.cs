using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class ActivationParticipant : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid UserId { get; set; }
    public Guid TeamId { get; set; }
    public string TeamNameSnapshot { get; set; } = "";
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Pending;
    public Guid? ResolvedSubstituteUserId { get; set; }
    public int CallAttemptCount { get; set; }
    public bool IsSubstitute { get; set; }
    public Guid? InductedFromParticipantId { get; set; }
}
