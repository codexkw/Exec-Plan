using ExecPlan.Application.Abstractions;

namespace ExecPlan.Infrastructure.Time;

public sealed class KuwaitClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
