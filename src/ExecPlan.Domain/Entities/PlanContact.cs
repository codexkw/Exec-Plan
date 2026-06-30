using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class PlanContact : BaseEntity
{
    public Guid PlanId { get; set; }
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public ContactKind Kind { get; set; }
}
