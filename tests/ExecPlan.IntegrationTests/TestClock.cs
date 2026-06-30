using ExecPlan.Application.Abstractions;

namespace ExecPlan.IntegrationTests;

/// <summary>Settable <see cref="IClock"/> test double so expiry/rotation assertions are deterministic.</summary>
public sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc);
}
