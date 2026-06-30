namespace ExecPlan.Application.Dashboard;

/// <summary>
/// The single server-side aggregated monitoring payload for one activation (design §5.5, FR-MON-1/2/3).
/// Computed in one read pass and returned by both the REST snapshot endpoint and the SignalR push.
///
/// The five participant counters are <b>mutually exclusive</b> (DEC-16): every participant is in exactly
/// one of <see cref="PendingCount"/>/<see cref="ReadyCount"/>/<see cref="EscalatedCount"/>/
/// <see cref="InductedCount"/>, so <c>Total == Pending + Ready + Escalated + Inducted</c>.
/// </summary>
public sealed record DashboardDto(
    Guid ActivationId,
    int TotalParticipants,
    int PendingCount,
    int ReadyCount,
    int EscalatedCount,
    int InductedCount,
    double ResponseRate,
    double TaskCompletionRate,
    IReadOnlyList<TeamRow> Teams,
    IReadOnlyList<OverdueTask> Overdue,
    IReadOnlyList<FeedEvent> Events);

/// <summary>
/// One per-team ranking row. <see cref="Score"/> = <c>0.5 * readyRatio + 0.5 * doneRatio</c> (each
/// ratio guarded to 0 when its denominator is 0). Rows are ordered best→delayed (Score descending), so
/// the first row is the best-performing team and the last is the most delayed.
/// </summary>
public sealed record TeamRow(
    Guid TeamId,
    string TeamName,
    int Members,
    int ReadyCount,
    int TasksTotal,
    int TasksDone,
    double Score);

/// <summary>A still-pending execution task whose <see cref="DueAtUtc"/> is already in the past.</summary>
public sealed record OverdueTask(
    Guid TaskId,
    string Title,
    Guid ParticipantUserId,
    DateTime DueAtUtc);

/// <summary>
/// A synthesized live-feed entry. The feed is the union of six event sources (notification, call,
/// response, escalation, task-completed, broadcast) projected to a common shape, newest first, capped
/// at 50.
/// </summary>
public sealed record FeedEvent(
    DateTime AtUtc,
    string Type,
    string Text);
