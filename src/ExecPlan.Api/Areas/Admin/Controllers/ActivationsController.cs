using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Task 14: the live Dashboard (server-rendered) + its JSON snapshot + the thin TeamLeader landing list.
/// Class gate admits SystemAdmin/PlanManager/TeamLeader — TeamMember has no admin-area surface at all
/// (already rejected at cookie login, Task 3). <see cref="EnsureMayViewAsync"/> replicates the proven
/// object-level "own teams" check from the Phase 1 REST surface
/// (<c>Api/Controllers/ActivationsController.Dashboard</c>, design §14 / DEC-17): a TeamLeader may view
/// an activation only if they lead at least one participating team; Manager/Admin are unrestricted.
/// <see cref="IDashboardService"/> itself stays actor-agnostic (Manager/Admin reads and the close-summary
/// reuse it as-is) — the authorization lives here, not in the service. The SAME guard runs before BOTH
/// <see cref="Dashboard"/> and <see cref="Snapshot"/> so a leader cannot bypass the HTML gate by hitting
/// the JSON endpoint directly. Task 15 adds the action-bar POST endpoints (run-escalation/broadcast/
/// close) the view already renders disabled/placeholder for; Task 16 adds the SignalR-driven
/// <c>dashboard.js</c> client the view already references.
/// </summary>
[Area("Admin")]
[Route("admin/activations")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Roles = "SystemAdmin,PlanManager,TeamLeader")]
public sealed class ActivationsController : Controller
{
    private readonly IDashboardService _dash;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _me;

    public ActivationsController(IDashboardService dash, IUnitOfWork uow, ICurrentUser me)
    {
        _dash = dash;
        _uow = uow;
        _me = me;
    }

    /// <summary>
    /// Leader landing (<c>GET /admin/activations</c>). SystemAdmin/PlanManager redirect to their existing
    /// richer <c>/admin/plans</c> list; a TeamLeader sees every currently-Active activation with at least
    /// one participating team they lead (computed the same way <see cref="EnsureMayViewAsync"/> does).
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager)))
        {
            return Redirect("/admin/plans");
        }

        var leaderId = _me.UserId ?? Guid.Empty;

        var activeActivations = await _uow.Repo<PlanActivation>()
            .ListAsync(a => a.Status == ActivationStatus.Active, ct);
        var activeIds = activeActivations.Select(a => a.Id).ToList();

        var participants = await _uow.Repo<ActivationParticipant>()
            .ListAsync(p => activeIds.Contains(p.ActivationId), ct);
        var teamIds = participants.Select(p => p.TeamId).Distinct().ToList();

        var ledTeamIds = (await _uow.Repo<Team>()
                .ListAsync(t => teamIds.Contains(t.Id) && t.TeamLeaderUserId == leaderId, ct))
            .Select(t => t.Id)
            .ToHashSet();

        var myActivationIds = participants
            .Where(p => ledTeamIds.Contains(p.TeamId))
            .Select(p => p.ActivationId)
            .Distinct()
            .ToHashSet();

        var vm = new LeaderActivationsVm
        {
            Activations = activeActivations
                .Where(a => myActivationIds.Contains(a.Id))
                .Select(a => new LeaderActivationsVm.Row(a.Id, a.Shift, a.RosterDate))
                .ToList(),
        };
        return View(vm);
    }

    // PRD §14 "own teams" / DEC-17 — a TeamLeader may view an activation only if they lead at least one
    // participating team. Manager/Admin: no restriction. Mirrors the proven API path
    // (Api/Controllers/ActivationsController.Dashboard) so the MVC panel gets the exact same
    // object-level scoping — both Dashboard and Snapshot below call this before touching the snapshot.
    private async Task EnsureMayViewAsync(Guid activationId, CancellationToken ct)
    {
        if (_me.Role != UserRole.TeamLeader)
        {
            return;
        }

        var participants = await _uow.Repo<ActivationParticipant>().ListAsync(p => p.ActivationId == activationId, ct);
        var teamIds = participants.Select(p => p.TeamId).Distinct().ToList();
        var teams = await _uow.Repo<Team>().ListAsync(t => teamIds.Contains(t.Id), ct);
        var leadsAny = _me.UserId is not null && teams.Any(t => t.TeamLeaderUserId == _me.UserId);
        if (!leadsAny)
        {
            throw AppException.Forbidden("You do not lead a team participating in this activation.");
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Dashboard(Guid id, CancellationToken ct)
    {
        await EnsureMayViewAsync(id, ct); // Forbidden -> AppExceptionMiddleware -> /admin/denied

        var dto = await _dash.GetSnapshotAsync(id, ct);
        if (dto.Status == ActivationStatus.Closed)
        {
            return Redirect($"/admin/activations/{id}/summary");
        }

        return View(new DashboardVm(dto,
            User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager))));
    }

    [HttpGet("{id:guid}/snapshot")]
    public async Task<IActionResult> Snapshot(Guid id, CancellationToken ct)
    {
        await EnsureMayViewAsync(id, ct); // same guard — dashboard.js (Task 16) polls this
        return Json(await _dash.GetSnapshotAsync(id, ct));
    }
}
