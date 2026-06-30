using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class Plan : BaseEntity
{
    public string Name { get; set; } = "";
    public PlanType Type { get; set; }
    public string Objective { get; set; } = "";
    public string Description { get; set; } = "";
    public string Scope { get; set; } = "";
    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public Guid CreatedByUserId { get; set; }
    public List<PlanContact> Contacts { get; set; } = new();
    public List<PlanActivator> Activators { get; set; } = new();
}
