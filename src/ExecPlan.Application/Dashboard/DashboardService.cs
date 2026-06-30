using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Dashboard;

/// <inheritdoc cref="IDashboardService"/>
/// <remarks>
/// Loads the activation, then each related set by <c>ActivationId</c> via separate no-tracking
/// <see cref="IRepository{T}.ListAsync"/> reads (per DEC-14: in-memory aggregation, no multi-collection
/// Includes → no cartesian explosion). Everything below is pure LINQ-to-objects over those lists, so the
/// Application layer stays EF-free. No mutation, no SaveChanges, no realtime push.
/// </remarks>
public sealed class DashboardService : IDashboardService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;

    public DashboardService(IUnitOfWork uow, IClock clock)
    {
        _uow = uow;
        _clock = clock;
    }

    public async Task<DashboardDto> GetSnapshotAsync(Guid activationId, CancellationToken ct = default)
    {
        // 1. Activation must exist. The set-loads below (ListAsync) are no-tracking; this is a pure
        //    read with no mutation/SaveChanges/realtime push.
        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(activationId, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        // 2. One filtered no-tracking read per set (DEC-14 — no cartesian Includes).
        var participants = await _uow.Repo<ActivationParticipant>()
            .ListAsync(p => p.ActivationId == activationId, ct);
        var tasks = await _uow.Repo<ExecutionTask>()
            .ListAsync(t => t.ActivationId == activationId, ct);
        var notifications = await _uow.Repo<NotificationLog>()
            .ListAsync(n => n.ActivationId == activationId, ct);
        var calls = await _uow.Repo<CallAttempt>()
            .ListAsync(c => c.ActivationId == activationId, ct);
        var responses = await _uow.Repo<ResponseStatus>()
            .ListAsync(r => r.ActivationId == activationId, ct);
        var escalations = await _uow.Repo<EscalationLog>()
            .ListAsync(e => e.ActivationId == activationId, ct);
        var broadcasts = await _uow.Repo<BroadcastMessage>()
            .ListAsync(b => b.ActivationId == activationId, ct);

        // 3. Counters — mutually exclusive (DEC-16): Total == Pending + Ready + Escalated + Inducted.
        var total = participants.Count;
        var pending = participants.Count(p => p.Status == ParticipantStatus.Pending);
        var ready = participants.Count(p => p.Status == ParticipantStatus.Ready);
        var escalated = participants.Count(p => p.Status == ParticipantStatus.Escalated);
        var inducted = participants.Count(p => p.Status == ParticipantStatus.Inducted);

        // 4. Rates (guard divide-by-zero → 0).
        var responseRate = total == 0 ? 0d : (double)ready / total;
        var doneTaskCount = tasks.Count(t => t.Status == ExecTaskStatus.Done);
        var taskCompletionRate = tasks.Count == 0 ? 0d : (double)doneTaskCount / tasks.Count;

        // userId lookup keyed by participant id — reused by overdue + response events.
        var userIdByParticipant = participants
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => g.First().UserId);
        var tasksByParticipant = tasks.ToLookup(t => t.ParticipantId);

        // 5. Per-team rows, ordered best→delayed (Score descending).
        var teams = participants
            .GroupBy(p => new { p.TeamId, p.TeamNameSnapshot })
            .Select(g =>
            {
                var members = g.Count();
                var teamReady = g.Count(p => p.Status == ParticipantStatus.Ready);
                var teamTasks = g.SelectMany(p => tasksByParticipant[p.Id]).ToList();
                var tasksTotal = teamTasks.Count;
                var tasksDone = teamTasks.Count(t => t.Status == ExecTaskStatus.Done);

                var readyRatio = members == 0 ? 0d : (double)teamReady / members;
                var doneRatio = tasksTotal == 0 ? 0d : (double)tasksDone / tasksTotal;
                var score = (0.5 * readyRatio) + (0.5 * doneRatio);

                return new TeamRow(
                    g.Key.TeamId, g.Key.TeamNameSnapshot, members, teamReady, tasksTotal, tasksDone, score);
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        // 6. Overdue: still-Pending tasks whose due time has passed (join to the owning participant's user).
        var now = _clock.UtcNow;
        var overdue = tasks
            .Where(t => t.Status == ExecTaskStatus.Pending && t.DueAtUtc < now)
            .Select(t => new OverdueTask(
                t.Id,
                t.Title,
                userIdByParticipant.TryGetValue(t.ParticipantId, out var uid) ? uid : Guid.Empty,
                t.DueAtUtc))
            .ToList();

        // 7. Synthesized feed: union the six sources, newest first, capped at 50.
        var events = new List<FeedEvent>(
            notifications.Count + calls.Count + responses.Count
            + escalations.Count + tasks.Count + broadcasts.Count);

        // Broadcast notifications are surfaced via the dedicated "broadcast" feed source below, so
        // exclude their NotificationLog rows here to avoid double-listing them.
        events.AddRange(notifications
            .Where(n => n.Kind != NotificationKind.Broadcast)
            .Select(n => new FeedEvent(n.CreatedAtUtc, "notification", n.Body)));
        events.AddRange(calls.Select(c => new FeedEvent(c.CreatedAtUtc, "call", $"attempt #{c.AttemptNumber}")));
        // Identity-free text — the raw recipient/user guid is not embedded in the feed.
        events.AddRange(responses.Select(r => new FeedEvent(r.AcknowledgedAtUtc, "response", "ready")));
        events.AddRange(escalations.Select(e => new FeedEvent(e.CreatedAtUtc, "escalation", "substitute inducted")));
        events.AddRange(tasks
            .Where(t => t.Status == ExecTaskStatus.Done && t.CompletedAtUtc.HasValue)
            .Select(t => new FeedEvent(t.CompletedAtUtc!.Value, "task", $"{t.Title} done")));
        events.AddRange(broadcasts.Select(b => new FeedEvent(b.CreatedAtUtc, "broadcast", b.Body)));

        var feed = events
            .OrderByDescending(e => e.AtUtc)
            .Take(50)
            .ToList();

        // 8. Assemble. Pure read — nothing saved.
        return new DashboardDto(
            activationId, activation.Status, activation.Shift, activation.RosterDate,
            total, pending, ready, escalated, inducted,
            responseRate, taskCompletionRate, teams, overdue, feed);
    }
}
