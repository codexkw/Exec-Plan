namespace ExecPlan.Application.Common;

/// <summary>
/// Tunable escalation policy. <see cref="DefaultThreshold"/> is the number of unanswered call
/// attempts a participant tolerates before auto-escalation to the frozen substitute (PRD signature
/// mechanic). Stamped onto each <c>PlanActivation</c> at activation time so a running activation is
/// immune to later config changes. Shared by ActivationService (writes the threshold) and the later
/// EscalationService (reads it). Default 5.
/// </summary>
public sealed class EscalationOptions
{
    public int DefaultThreshold { get; set; } = 5;
}
