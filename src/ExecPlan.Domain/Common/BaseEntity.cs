namespace ExecPlan.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = default; // set by service/clock at insert; default avoids DateTime.Now in ctor
    public DateTime? UpdatedAtUtc { get; set; }
}
