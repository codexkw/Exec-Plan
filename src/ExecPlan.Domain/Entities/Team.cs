using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class Team : BaseEntity
{
    public Guid PlanId { get; set; }
    public string Name { get; set; } = "";
    public Guid? TeamLeaderUserId { get; set; }
}
