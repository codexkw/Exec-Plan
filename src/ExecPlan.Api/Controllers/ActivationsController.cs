using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Broadcast;
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
    private readonly AcknowledgeService _acknowledge;
    private readonly IEscalationService _escalation;
    private readonly IDashboardService _dashboard;
    private readonly ExecutionService _execution;
    private readonly BroadcastService _broadcast;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _cur;

    public ActivationsController(
        AcknowledgeService acknowledge,
        IEscalationService escalation,
        IDashboardService dashboard,
        ExecutionService execution,
        BroadcastService broadcast,
        IUnitOfWork uow,
        ICurrentUser cur)
    {
        _acknowledge = acknowledge;
        _escalation = escalation;
        _dashboard = dashboard;
        _execution = execution;
        _broadcast = broadcast;
        _uow = uow;
        _cur = cur;
    }

    public sealed record BroadcastRequest(string Body);

    public sealed record RaiseIssueRequest(string Body);

    public sealed record SetSubstituteRequest(Guid ParticipantId, Guid SubstituteUserId);

    public sealed record ExecutionTaskDto(
        Guid Id, Guid ActivationId, Guid ParticipantId, string Title, int Order,
        ExecTaskStatus Status, string? Note, DateTime DueAtUtc, DateTime? CompletedAtUtc);

    public sealed record NotificationDto(Guid Id, NotificationKind Kind, string Body, DateTime CreatedAtUtc);

    [HttpGet("{id:guid}/dashboard")]
    [Authorize(Roles = "SystemAdmin,PlanManager,TeamLeader")]
    public async Task<ActionResult<DashboardDto>> Dashboard(Guid id, CancellationToken ct) =>
        Ok(await _dashboard.GetSnapshotAsync(id, ct));

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
