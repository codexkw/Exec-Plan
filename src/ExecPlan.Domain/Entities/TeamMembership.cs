using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class TeamMembership : BaseEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
}
