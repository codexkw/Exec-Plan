using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Escalation;

/// <summary>
/// Implements one escalation cycle (design §5.4). Loads the Active activation (else NotFound /
/// Conflict), then for every still-<see cref="ParticipantStatus.Pending"/> participant stages another
/// <see cref="CallAttempt"/> and bumps <c>CallAttemptCount</c>. When that count reaches the
/// activation's copied <see cref="PlanActivation.EscalationThreshold"/> the participant is flipped to
/// <see cref="ParticipantStatus.Escalated"/> and — if a frozen substitute was resolved and hasn't
/// already been inducted — a new substitute <see cref="ActivationParticipant"/> is created with a
/// FRESH task set generated from the team's templates, a notification, a first call attempt, and an
/// <see cref="EscalationLog"/> linking the two. Everything is staged into the unit of work and
/// committed with exactly ONE <see cref="IUnitOfWork.SaveChangesAsync"/> (EF implicit transaction =
/// atomic, NFR-8); the realtime dashboard push happens only after the commit succeeds.
/// </summary>
public sealed class EscalationService : IEscalationService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly INotificationProvider _provider;
    private readonly IRealtimeNotifier _realtime;

    public EscalationService(
        IUnitOfWork uow,
        IClock clock,
        INotificationProvider provider,
        IRealtimeNotifier realtime)
    {
        _uow = uow;
        _clock = clock;
        _provider = provider;
        _realtime = realtime;
    }

    public async Task<EscalationCycleResult> RunCycleAsync(Guid activationId, CancellationToken ct = default)
    {
        // 1. Activation must exist and still be Active (you cannot escalate a closed activation).
        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(activationId, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        if (activation.Status != ActivationStatus.Active)
        {
            throw AppException.Conflict("Cannot escalate a closed activation.");
        }

        // 2. Pending participants TRACKED — escalation MUTATES them, so a no-tracking read would drop
        //    the CallAttemptCount/Status edits at SaveChanges.
        var pending = await _uow.Repo<ActivationParticipant>().ListTrackedAsync(
            p => p.ActivationId == activationId && p.Status == ParticipantStatus.Pending, ct);

        var now = _clock.UtcNow;
        var attemptsAdded = 0;
        var inducted = 0;

        foreach (var p in pending)
        {
            // 3a. One more call attempt for this pending participant.
            p.CallAttemptCount++;
            _provider.StageCallAttempt(activationId, p.Id, p.CallAttemptCount, now);
            attemptsAdded++;

            // 3b. At/over threshold: escalate, and induct the frozen substitute once.
            if (p.CallAttemptCount >= activation.EscalationThreshold)
            {
                p.Status = ParticipantStatus.Escalated;

                if (p.ResolvedSubstituteUserId is null)
                {
                    continue;
                }

                var alreadyInducted = await _uow.Repo<ActivationParticipant>()
                    .FirstOrDefaultAsync(x => x.InductedFromParticipantId == p.Id, ct);
                if (alreadyInducted is not null)
                {
                    continue;
                }

                var sub = new ActivationParticipant
                {
                    ActivationId = activationId,
                    UserId = p.ResolvedSubstituteUserId.Value,
                    TeamId = p.TeamId,
                    TeamNameSnapshot = p.TeamNameSnapshot,
                    // DEC-16: an inducted substitute is its own status category (Inducted), not Pending.
                    // Keeps the dashboard's five counters mutually exclusive (Total = Pending+Ready+
                    // Escalated+Inducted) and excludes substitutes from re-escalation (the cycle only
                    // loads Status==Pending). Lifecycle: Inducted → Ready on acknowledge.
                    Status = ParticipantStatus.Inducted,
                    IsSubstitute = true,
                    InductedFromParticipantId = p.Id,
                    ResolvedSubstituteUserId = null,
                    CallAttemptCount = 1,
                };
                await _uow.Repo<ActivationParticipant>().AddAsync(sub, ct);

                // Generate the substitute's task set FRESH from the team templates (DueAt = now + Duration).
                var templates = await _uow.Repo<TaskTemplate>().ListAsync(tt => tt.TeamId == p.TeamId, ct);
                foreach (var tpl in templates)
                {
                    var task = new ExecutionTask
                    {
                        ActivationId = activationId,
                        ParticipantId = sub.Id,
                        Title = tpl.Title,
                        Order = tpl.Order,
                        Status = ExecTaskStatus.Pending,
                        DueAtUtc = now + tpl.Duration,
                        SourceTaskTemplateId = tpl.Id,
                    };
                    await _uow.Repo<ExecutionTask>().AddAsync(task, ct);
                }

                _provider.StageNotification(
                    activationId, sub.UserId, NotificationKind.Notification, InductionNoticeBody, now);
                _provider.StageCallAttempt(activationId, sub.Id, 1, now);

                var log = new EscalationLog
                {
                    ActivationId = activationId,
                    ParticipantId = p.Id,
                    SubstituteUserId = sub.UserId,
                    NewParticipantId = sub.Id,
                    CreatedAtUtc = _clock.UtcNow,
                };
                await _uow.Repo<EscalationLog>().AddAsync(log, ct);
                inducted++;
            }
        }

        // 4. Commit everything atomically, then push realtime.
        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activationId, ct);

        return new EscalationCycleResult(attemptsAdded, inducted);
    }

    private const string InductionNoticeBody = "تم استدعاؤك كبديل. الرجاء تأكيد الجاهزية.";
}
