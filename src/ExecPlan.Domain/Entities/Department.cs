using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class Department : BaseEntity
{
    public string Name { get; set; } = "";
    public Guid OrganizationId { get; set; }
}
