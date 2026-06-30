using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class PlanActivation : BaseEntity
{
    public Guid PlanId { get; set; }
    public ActivationStatus Status { get; set; } = ActivationStatus.Active;
    public ShiftBand Shift { get; set; }
    public DateTime RosterDate { get; set; }
    public Guid ActivatedByUserId { get; set; }
    public DateTime ActivatedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public int EscalationThreshold { get; set; }
}
