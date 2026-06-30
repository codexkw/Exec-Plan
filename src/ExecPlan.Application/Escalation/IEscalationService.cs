namespace ExecPlan.Application.Escalation;

/// <summary>
/// One escalation cycle (design §5.4, FR-ESC-1..4). Adds a call attempt to every still-Pending
/// participant of an Active activation and, at the activation's copied threshold, marks the
/// non-responder <c>Escalated</c> and inducts their frozen substitute as a new participant with a
/// full task set, notification, and first call attempt. Identical behavior whether triggered from
/// the dashboard or the CLI. All staged into one transaction; the realtime push happens after commit.
/// </summary>
public interface IEscalationService
{
    Task<EscalationCycleResult> RunCycleAsync(Guid activationId, CancellationToken ct = default);
}

/// <summary>Outcome of one cycle: how many call attempts were added and how many substitutes inducted.</summary>
public record EscalationCycleResult(int AttemptsAdded, int Inducted);
