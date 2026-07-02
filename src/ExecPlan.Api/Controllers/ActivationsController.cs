using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Broadcast;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
using ExecPlan.Application.Escalation;
using ExecPlan.Application.Execution;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>
/// Every operation scoped to a running activation (design §5.4-5.6): the live dashboard snapshot,
/// the single counted readiness tap, manual escalation cycles, broadcast/raise-issue/set-substitute,
/// the participant-scoped «my tasks»/«my notifications» reads, and close. Controllers stay thin —
/// each delegates to its Application service (which owns the object-level authorization and the single
/// atomic transaction) and returns a DTO/projection, never a raw entity. The acting user for
/// acknowledge comes from <see cref="ICurrentUser"/> (401 if no authenticated principal).
/// </summary>
[ApiController]
[Route("api/v1/activations")]
public sealed class ActivationsController : ControllerBase
{
    /// <summary>How long a Closed activation stays visible on the <c>mine</c> discovery list, so a
    /// just-inducted substitute (and everyone else) can still find an activation right after it closes.</summary>
    private const int RecentlyClosedWindowHours = 12;

    private readonly AcknowledgeService _acknowledge;
    private readonly IEscalationService _escalation;
    private readonly IDashboardService _dashboard;
    private readonly ExecutionService _execution;
    private readonly BroadcastService _broadcast;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _cur;
    private readonly IClock _clock;

    public ActivationsController(
        AcknowledgeService acknowledge,
        IEscalationService escalation,
        IDashboardService dashboard,
        ExecutionService execution,
        BroadcastService broadcast,
        IUnitOfWork uow,
        ICurrentUser cur,
        IClock clock)
    {
        _acknowledge = acknowledge;
        _escalation = escalation;
        _dashboard = dashboard;
        _execution = execution;
        _broadcast = broadcast;
        _uow = uow;
        _cur = cur;
        _clock = clock;
    }

    public sealed record BroadcastRequest(string Body);

    public sealed record RaiseIssueRequest(string Body);

    public sealed record SetSubstituteRequest(Guid ParticipantId, Guid SubstituteUserId);

    public sealed record ExecutionTaskDto(
        Guid Id, Guid ActivationId, Guid ParticipantId, string Title, int Order,
        ExecTaskStatus Status, string? Note, DateTime DueAtUtc, DateTime? CompletedAtUtc);

    public sealed record NotificationDto(Guid Id, NotificationKind Kind, string Body, DateTime CreatedAtUtc);

    /// <summary>One row of the caller's activation-discovery list (Phase 3 gap G1). <see cref="MyRole"/> is
    /// the caller's relationship to THIS activation — <c>"Leader"</c> if they lead a participating team,
    /// else <c>"Participant"</c> if they are a participant, else <c>"Manager"</c> (a manager/admin seeing it
    /// by role). <see cref="MyParticipantId"/> is the caller's own participant id (null for a pure manager),
    /// so a Member/Leader can drive my-tasks/acknowledge without a second lookup. <see cref="PlanName"/> is a
    /// live read of the current plan name (the runtime stores no plan-name snapshot).</summary>
    public sealed record MyActivationListItemDto(
        Guid ActivationId, Guid PlanId, string PlanName, ActivationStatus Status, ShiftBand Shift,
        DateTime RosterDate, string MyRole, DateTime StartedAtUtc, DateTime? ClosedAtUtc, Guid? MyParticipantId);

    /// <summary>One roster row for the participant-list read (Phase 3 gap G5). <see cref="ParticipantId"/>
    /// is the id that <c>set-substitute</c> and task-reassign require. <see cref="FullName"/> is a live read
    /// (the runtime snapshots only the team name, not the member name); <see cref="TeamName"/> is the frozen
    /// <c>TeamNameSnapshot</c>.</summary>
    public sealed record ParticipantRosterRowDto(
        Guid ParticipantId, Guid UserId, string FullName, Guid TeamId, string TeamName,
        ParticipantStatus Status, bool IsSubstitute, Guid? InductedFromParticipantId, int TasksTotal, int TasksDone);

    /// <summary>A user eligible to be designated a live substitute for a team (Phase 3 gap G5b): an active
    /// member of the team who is not already on duty in this activation. Feeds the leader's set-substitute
    /// picker, which the Manager/Admin-only member-listing endpoints could not.</summary>
    public sealed record SubstituteCandidateDto(Guid UserId, string FullName);

    [HttpGet("mine")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<MyActivationListItemDto>>> Mine(CancellationToken ct)
    {
        if (_cur.UserId is not Guid userId)
        {
            return Unauthorized();
        }

        // "Current" = Active, or Closed within the recency window. Pushed into each DB query below so we
        // never materialize the ever-growing PlanActivation history in memory (DEC-29).
        var cutoff = _clock.UtcNow.AddHours(-RecentlyClosedWindowHours);

        // The caller's own participant rows (across all activations) — drives both the Participant/Leader
        // role tag and MyParticipantId.
        var myParticipants = await _uow.Repo<ActivationParticipant>().ListAsync(p => p.UserId == userId, ct);
        var myParticipantByActivation = myParticipants
            .GroupBy(p => p.ActivationId)
            .ToDictionary(g => g.Key, g => g.First());

        // Teams the caller leads — an activation is theirs "as leader" if any of its participants sit on
        // one of these teams (mirrors the dashboard's DEC-17 object-level scoping).
        var ledTeams = await _uow.Repo<Team>().ListAsync(t => t.TeamLeaderUserId == userId, ct);
        var ledTeamIds = ledTeams.Select(t => t.Id).ToHashSet();

        var isManager = _cur.Role is UserRole.PlanManager or UserRole.SystemAdmin;

        List<PlanActivation> activations;
        if (isManager)
        {
            // Managers see all plans (locked decision), so all activations — active + recently closed.
            // Filter in the DB, not by materializing the whole table (DEC-29).
            activations = (await _uow.Repo<PlanActivation>().ListAsync(
                a => a.Status == ActivationStatus.Active
                     || (a.Status == ActivationStatus.Closed && a.ClosedAtUtc != null && a.ClosedAtUtc >= cutoff),
                ct)).ToList();
        }
        else
        {
            // A Member discovers activations they participate in; a Leader ALSO discovers activations
            // where a team they lead participates, even if the leader is not personally a participant.
            var myActivationIds = myParticipants.Select(p => p.ActivationId).Distinct().ToHashSet();
            if (ledTeamIds.Count > 0)
            {
                var ledParticipants = await _uow.Repo<ActivationParticipant>()
                    .ListAsync(p => ledTeamIds.Contains(p.TeamId), ct);
                foreach (var activationId in ledParticipants.Select(p => p.ActivationId))
                {
                    myActivationIds.Add(activationId);
                }
            }

            activations = (await _uow.Repo<PlanActivation>().ListAsync(
                a => myActivationIds.Contains(a.Id)
                     && (a.Status == ActivationStatus.Active
                         || (a.Status == ActivationStatus.Closed && a.ClosedAtUtc != null && a.ClosedAtUtc >= cutoff)),
                ct)).ToList();
        }

        if (activations.Count == 0)
        {
            return Ok(Array.Empty<MyActivationListItemDto>());
        }

        // Which of these activations has a participant on a team the caller leads?
        var activationIds = activations.Select(a => a.Id).ToHashSet();
        var participantsInScope = await _uow.Repo<ActivationParticipant>()
            .ListAsync(p => activationIds.Contains(p.ActivationId), ct);
        var ledActivationIds = participantsInScope
            .Where(p => ledTeamIds.Contains(p.TeamId))
            .Select(p => p.ActivationId)
            .ToHashSet();

        // Live plan names (the runtime has no plan-name snapshot).
        var planIds = activations.Select(a => a.PlanId).Distinct().ToHashSet();
        var plans = await _uow.Repo<Plan>().ListAsync(p => planIds.Contains(p.Id), ct);
        var planNameById = plans.ToDictionary(p => p.Id, p => p.Name);

        string RoleFor(PlanActivation a)
        {
            if (ledActivationIds.Contains(a.Id))
            {
                return "Leader";
            }

            return myParticipantByActivation.ContainsKey(a.Id) ? "Participant" : "Manager";
        }

        var result = activations
            .OrderBy(a => a.Status == ActivationStatus.Active ? 0 : 1)
            .ThenByDescending(a => a.ActivatedAtUtc)
            .Select(a => new MyActivationListItemDto(
                a.Id,
                a.PlanId,
                planNameById.TryGetValue(a.PlanId, out var name) ? name : string.Empty,
                a.Status,
                a.Shift,
                a.RosterDate,
                RoleFor(a),
                a.ActivatedAtUtc,
                a.ClosedAtUtc,
                myParticipantByActivation.TryGetValue(a.Id, out var mp) ? mp.Id : null))
            .ToList();

        return Ok(result);
    }

    [HttpGet("{id:guid}/participants")]
    [Authorize(Roles = "SystemAdmin,PlanManager,TeamLeader")]
    public async Task<ActionResult<IReadOnlyList<ParticipantRosterRowDto>>> Participants(Guid id, CancellationToken ct)
    {
        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(id, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        var participants = await _uow.Repo<ActivationParticipant>().ListAsync(p => p.ActivationId == id, ct);

        // Leader object-level scoping (DEC-17, same rule as the dashboard): a TeamLeader sees only
        // participants of teams they lead, and must lead at least one participating team or the request
        // 403s. Manager/Admin see the whole roster.
        if (_cur.Role == UserRole.TeamLeader)
        {
            var ledTeams = await _uow.Repo<Team>().ListAsync(t => t.TeamLeaderUserId == _cur.UserId, ct);
            var ledTeamIds = ledTeams.Select(t => t.Id).ToHashSet();
            participants = participants.Where(p => ledTeamIds.Contains(p.TeamId)).ToList();
            if (participants.Count == 0)
            {
                throw AppException.Forbidden("You do not lead a team participating in this activation.");
            }
        }

        if (participants.Count == 0)
        {
            return Ok(Array.Empty<ParticipantRosterRowDto>());
        }

        var userIds = participants.Select(p => p.UserId).Distinct().ToHashSet();
        var users = await _uow.Repo<User>().ListAsync(u => userIds.Contains(u.Id), ct);
        var nameByUser = users.ToDictionary(u => u.Id, u => u.FullName);

        var tasks = await _uow.Repo<ExecutionTask>().ListAsync(t => t.ActivationId == id, ct);
        var tasksByParticipant = tasks.ToLookup(t => t.ParticipantId);

        var roster = participants
            .Select(p =>
            {
                var participantTasks = tasksByParticipant[p.Id].ToList();
                return new ParticipantRosterRowDto(
                    p.Id,
                    p.UserId,
                    nameByUser.TryGetValue(p.UserId, out var name) ? name : string.Empty,
                    p.TeamId,
                    p.TeamNameSnapshot,
                    p.Status,
                    p.IsSubstitute,
                    p.InductedFromParticipantId,
                    participantTasks.Count,
                    participantTasks.Count(t => t.Status == ExecTaskStatus.Done));
            })
            .OrderBy(r => r.TeamName)
            .ThenBy(r => r.FullName)
            .ToList();

        return Ok(roster);
    }

    [HttpGet("{id:guid}/teams/{teamId:guid}/eligible-substitutes")]
    [Authorize(Roles = "SystemAdmin,PlanManager,TeamLeader")]
    public async Task<ActionResult<IReadOnlyList<SubstituteCandidateDto>>> EligibleSubstitutes(
        Guid id, Guid teamId, CancellationToken ct)
    {
        var activation = await _uow.Repo<PlanActivation>().GetByIdAsync(id, ct);
        if (activation is null)
        {
            throw AppException.NotFound("Activation not found.");
        }

        var team = await _uow.Repo<Team>().GetByIdAsync(teamId, ct);
        if (team is null)
        {
            throw AppException.NotFound("Team not found.");
        }

        // A TeamLeader may only query a team they lead; Manager/Admin may query any team.
        if (_cur.Role == UserRole.TeamLeader && team.TeamLeaderUserId != _cur.UserId)
        {
            throw AppException.Forbidden("You do not lead this team.");
        }

        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => m.TeamId == teamId, ct);
        var memberUserIds = memberships.Select(m => m.UserId).Distinct().ToHashSet();
        if (memberUserIds.Count == 0)
        {
            return Ok(Array.Empty<SubstituteCandidateDto>());
        }

        // Exclude anyone already on duty in this activation — a current participant can't cover for another.
        var currentParticipants = await _uow.Repo<ActivationParticipant>().ListAsync(p => p.ActivationId == id, ct);
        var onDutyUserIds = currentParticipants.Select(p => p.UserId).ToHashSet();

        var users = await _uow.Repo<User>().ListAsync(u => memberUserIds.Contains(u.Id), ct);
        var candidates = users
            .Where(u => u.IsActive && !onDutyUserIds.Contains(u.Id))
            .OrderBy(u => u.FullName)
            .Select(u => new SubstituteCandidateDto(u.Id, u.FullName))
            .ToList();

        return Ok(candidates);
    }

    [HttpGet("{id:guid}/dashboard")]
    [Authorize(Roles = "SystemAdmin,PlanManager,TeamLeader")]
    public async Task<ActionResult<DashboardDto>> Dashboard(Guid id, CancellationToken ct)
    {
        // Manager/Admin see any activation; a TeamLeader is further scoped to activations where they
        // lead at least one participating team (PRD §14 "own teams" — see DEC-17). IDashboardService
        // itself stays actor-agnostic (the close summary and manager/admin reads reuse it as-is), so
        // this object-level check happens here, before the snapshot is computed.
        if (_cur.Role == UserRole.TeamLeader)
        {
            var participants = await _uow.Repo<ActivationParticipant>().ListAsync(p => p.ActivationId == id, ct);
            var teamIds = participants.Select(p => p.TeamId).Distinct().ToList();
            var teams = await _uow.Repo<Team>().ListAsync(t => teamIds.Contains(t.Id), ct);
            var leadsAny = _cur.UserId is not null && teams.Any(t => t.TeamLeaderUserId == _cur.UserId);
            if (!leadsAny)
            {
                throw AppException.Forbidden("You do not lead a team participating in this activation.");
            }
        }

        return Ok(await _dashboard.GetSnapshotAsync(id, ct));
    }

    [HttpPost("{id:guid}/acknowledge")]
    [Authorize]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        if (_cur.UserId is not Guid actingUserId)
        {
            return Unauthorized();
        }

        await _acknowledge.AcknowledgeAsync(id, actingUserId, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/run-escalation")]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<ActionResult<EscalationCycleResult>> RunEscalation(Guid id, CancellationToken ct) =>
        Ok(await _escalation.RunCycleAsync(id, ct));

    [HttpPost("{id:guid}/broadcast")]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Broadcast(Guid id, [FromBody] BroadcastRequest request, CancellationToken ct)
    {
        await _broadcast.BroadcastAsync(id, request.Body, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/set-substitute")]
    [Authorize]
    public async Task<IActionResult> SetSubstitute(Guid id, [FromBody] SetSubstituteRequest request, CancellationToken ct)
    {
        await _execution.SetSubstituteLiveAsync(id, request.ParticipantId, request.SubstituteUserId, ct);
        return Ok();
    }

    [HttpPost("{id:guid}/raise-issue")]
    [Authorize]
    public async Task<IActionResult> RaiseIssue(Guid id, [FromBody] RaiseIssueRequest request, CancellationToken ct)
    {
        await _execution.RaiseIssueAsync(id, request.Body, ct);
        return Ok();
    }

    [HttpGet("{id:guid}/my-tasks")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<ExecutionTaskDto>>> MyTasks(Guid id, CancellationToken ct)
    {
        if (_cur.UserId is not Guid userId)
        {
            return Unauthorized();
        }

        // Find the caller's participant in this activation; non-participants get an empty list (not a 403).
        var participant = await _uow.Repo<ActivationParticipant>()
            .FirstOrDefaultAsync(p => p.ActivationId == id && p.UserId == userId, ct);
        if (participant is null)
        {
            return Ok(Array.Empty<ExecutionTaskDto>());
        }

        var tasks = await _uow.Repo<ExecutionTask>()
            .ListAsync(t => t.ActivationId == id && t.ParticipantId == participant.Id, ct);

        return Ok(tasks
            .OrderBy(t => t.Order)
            .Select(t => new ExecutionTaskDto(
                t.Id, t.ActivationId, t.ParticipantId, t.Title, t.Order, t.Status, t.Note, t.DueAtUtc, t.CompletedAtUtc))
            .ToList());
    }

    [HttpGet("{id:guid}/my-notifications")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<NotificationDto>>> MyNotifications(Guid id, CancellationToken ct)
    {
        if (_cur.UserId is not Guid userId)
        {
            return Unauthorized();
        }

        var notifications = await _uow.Repo<NotificationLog>()
            .ListAsync(n => n.ActivationId == id && n.RecipientUserId == userId, ct);

        return Ok(notifications
            .OrderByDescending(n => n.CreatedAtUtc)
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Body, n.CreatedAtUtc))
            .ToList());
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<ActionResult<DashboardDto>> Close(Guid id, CancellationToken ct) =>
        Ok(await _execution.CloseAsync(id, ct));
}
