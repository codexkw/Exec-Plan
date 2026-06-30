using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class PlanActivator : BaseEntity
{
    public Guid PlanId { get; set; }
    public Guid UserId { get; set; }
}
