using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Shifts;

/// <summary>
/// Result of resolving a UTC instant to a Kuwait shift band and the roster date it belongs to.
/// </summary>
public sealed record ShiftResolution(ShiftBand Band, DateTime RosterDate);
