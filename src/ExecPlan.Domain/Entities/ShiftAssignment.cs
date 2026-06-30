using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class ShiftAssignment : BaseEntity
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public ShiftBand Shift { get; set; }
    public DateTime Date { get; set; }
    public Guid? SubstituteForUserId { get; set; }
}
