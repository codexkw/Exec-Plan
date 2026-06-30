using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class TaskTemplate : BaseEntity
{
    public Guid TeamId { get; set; }
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public TimeSpan Duration { get; set; }
}
