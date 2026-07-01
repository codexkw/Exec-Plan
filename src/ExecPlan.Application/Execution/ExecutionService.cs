using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Execution;

/// <summary>
/// Live-execution operations performed against a running activation (design §5.6): a member updates
/// their own task (done/note), a team leader (or manager/admin) reassigns a task across the team they
/// lead, sets a live substitute, or raises an issue to the plan creator, and a manager/admin closes
/// the activation. The acting user is read from <see cref="ICurrentUser"/> (object-level authorization);
/// methods take only operation parameters. Every write stages into the unit of work and commits with a
/// single <see cref="IUnitOfWork.SaveChangesAsync"/> (EF implicit transaction = atomic, NFR-8); any
/// mutated entity is loaded TRACKED (<see cref="IRepository{T}.GetByIdAsync"/>) so the edit persists,
/// and the realtime push fires only after the commit succeeds.
/// </summary>
public sealed class ExecutionService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly ICurrentUser _cur;
    private readonly INotificationProvider _provider;
    private readonly IRealtimeNotifier _realtime;
    private readonly IDashboardService _dashboard;

    public ExecutionService(
        IUnitOfWork uow,
        IClock clock,
        ICurrentUser cur,
        INotificationProvider provider,
        IRealtimeNotifier realtime,
        IDashboardService dashboard)
    {
        _uow = uow;
        _clock = clock;
        _cur = cur;
        _provider = provider;
        _realtime = realtime;
        _dashboard = dashboard;
    }

    /// <summary>
    /// Update an execution task: toggle done (stamps/clears <c>CompletedAtUtc</c>), set its note, and/or
    /// reassign it to another participant. Done/note-only requires owner, the source team's leader, or
    /// manager/admin. Reassign requires the target be in the SAME activation (else Validation); a leader
    /// must lead BOTH the source and target teams (else Forbidden — no cross-team boundary), while a
    /// manager/admin may reassign anywhere.
    /// </summary>
    public async Task UpdateTaskAsync(
        Guid taskId, bool? done, string? note, Guid? reassignToParticipantId, CancellationToken ct = default)
    {
        var task = await _uow.Repo<ExecutionTask>().GetByIdAsync(taskId, ct);
        if (task is null)
        {
            throw AppException.NotFound("Task not found.");
        }

        var source = await _uow.Repo<ActivationParticipant>().GetByIdAsync(task.ParticipantId, ct);
        if (source is null)
        {
            throw AppException.NotFound("Task owner participant not found.");
        }

        var isOwner = _cur.UserId is not null && _cur.UserId == source.UserId;
        var isMgrAdmin = _cur.Role is UserRole.PlanManager or UserRole.SystemAdmin;
        var sourceTeam = await _uow.Repo<Team>().GetByIdAsync(source.TeamId, ct);
        var isLeaderOfSource = _cur.UserId is not null && sourceTeam?.TeamLeaderUserId == _cur.UserId;

        if (reassignToParticipantId is null)
        {
            // Done/note-only: owner, the source team's leader, or manager/admin.
            if (!(isOwner || isLeaderOfSource || isMgrAdmin))
            {
                throw AppException.Forbidden("You are not allowed to update this task.");
            }
        }
        else
        {
            var target = await _uow.Repo<ActivationParticipant>().GetByIdAsync(reassignToParticipantId.Value, ct);
            if (target is null)
            {
                throw AppException.Validation("Target participant not found.");
            }

            if (target.ActivationId != task.ActivationId)
            {
                throw AppException.Validation("Target participant belongs to a different activation.");
            }

            if (isMgrAdmin)
            {
                // Manager/admin may reassign across any team boundary.
            }
            else if (isLeaderOfSource)
            {
                // A leader may only reassign WITHIN the teams they lead — they must lead the target team too.
                var targetTeam = await _uow.Repo<Team>().GetByIdAsync(target.TeamId, ct);
                var isLeaderOfTarget = _cur.UserId is not null && targetTeam?.TeamLeaderUserId == _cur.UserId;
                if (!isLeaderOfTarget)
                {
                    throw AppException.Forbidden("You cannot reassign a task across a team boundary.");
                }
            }
            else
            {
                throw AppException.Forbidden("You are not allowed to reassign this task.");
            }

            task.ParticipantId = reassignToParticipantId.Value;
        }

        if (done == true)
        {
            task.Status = ExecTaskStatus.Done;
            task.CompletedAtUtc = _clock.UtcNow;
        }
        else if (done == false)
        {
            task.Status = ExecTaskStatus.Pending;
            task.CompletedAtUtc = null;
        }

        if (note != null)
        {
            task.Note = note;
        }

        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(task.ActivationId, ct);
    }

    /// <summary>
    /// Set the live (resolved) substitute for a participant of an activation. Manager/admin, or the
    /// leader of the participant's team. Updates <c>ResolvedSubstituteUserId</c> so a later escalation
    /// cycle inducts this stand-in.
    /// </summary>
    public async Task SetSubstituteLiveAsync(
        Guid activationId, Guid participantId, Guid substituteUserId, CancellationToken ct = default)
    {
        var participant = await _uow.Repo<ActivationParticipant>().GetByIdAsync(participantId, ct);
        if (participant is null)
        {
            throw AppException.NotFound("Participant not found.");
        }

        if (participant.ActivationId != activationId)
        {
            throw AppException.Validation("Participant belongs to a different activation.");
        }

        var isMgrAdmin = _cur.Role is UserRole.PlanManager or UserRole.SystemAdmin;
        var team = await _uow.Repo<Team>().GetByIdAsync(participant.TeamId, ct);
        var isLeaderOfTeam = _cur.UserId is not null && team?.TeamLeaderUserId == _cur.UserId;
        if (!(isMgrAdmin || isLeaderOfTeam))
        {
            throw AppException.Forbidden("You are not allowed to set a substitute for this participant.");
        }

        participant.ResolvedSubstituteUserId = substituteUserId;

        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activationId, ct);
    }

    /// <summary>
    /// A team leader raises an issue against a running activation. Recorded as a
    /// <see cref="NotificationKind.Notification"/> addressed to the plan creator so it surfaces in the
    /// monitoring feed.
    /// </summary>
    public async Task RaiseIssueAsync(Guid activationId, string body, CancellationToken ct = default)
    {
        if (_cur.Role != UserRole.TeamLeader)
        {
            throw AppException.Forbidden("Only a team leader may raise an issue.");
        }

        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(activationId, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        var plan = await _uow.Repo<Plan>().GetByIdAsync(activation.PlanId, ct);
        if (plan is null)
        {
            throw AppException.NotFound("Plan not found.");
        }

        _provider.StageNotification(
            activationId, plan.CreatedByUserId, NotificationKind.Notification, "Issue: " + body, _clock.UtcNow);

        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activationId, ct);
    }

    /// <summary>
    /// Close a running activation (manager/admin only). Sets <c>Status=Closed</c> + <c>ClosedAtUtc</c>,
    /// pushes <see cref="IRealtimeNotifier.ActivationClosedAsync"/>, and returns the final aggregated
    /// dashboard snapshot. Closing an already-closed activation is a <see cref="AppException.Conflict"/>.
    /// </summary>
    public async Task<DashboardDto> CloseAsync(Guid activationId, CancellationToken ct = default)
    {
        var isMgrAdmin = _cur.Role is UserRole.PlanManager or UserRole.SystemAdmin;
        if (!isMgrAdmin)
        {
            throw AppException.Forbidden("Only a manager or admin may close an activation.");
        }

        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(activationId, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        if (activation.Status == ActivationStatus.Closed)
        {
            throw AppException.Conflict("This activation is already closed.", "AlreadyClosed");
        }

        activation.Status = ActivationStatus.Closed;
        activation.ClosedAtUtc = _clock.UtcNow;

        await _uow.SaveChangesAsync(ct);
        await _realtime.ActivationClosedAsync(activationId, ct);

        return await _dashboard.GetSnapshotAsync(activationId, ct);
    }
}
