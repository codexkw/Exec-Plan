using ExecPlan.Api.Areas.Admin.Models;
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
/// the JSON endpoint directly. Task 15 adds the action-bar POST endpoints (<see cref="RunEscalation"/>/
/// <see cref="Broadcast"/>/<see cref="Close"/>) the view rendered disabled/placeholder for, plus
/// <see cref="Summary"/> (the static post-close view — §16 "no live updates"); every new action is
/// <c>ManagerOrAdmin</c>-only (never TeamLeader), on top of the class-level role gate. Task 16 adds the
/// SignalR-driven <c>dashboard.js</c> client the view already references.
/// </summary>
[Area("Admin")]
[Route("admin/activations")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Roles = "SystemAdmin,PlanManager,TeamLeader")]
public sealed class ActivationsController : Controller
{
    private readonly IDashboardService _dash;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _me;
    private readonly IEscalationService _esc;
    private readonly BroadcastService _broadcast;
    private readonly ExecutionService _exec;

    public ActivationsController(
        IDashboardService dash,
        IUnitOfWork uow,
        ICurrentUser me,
        IEscalationService esc,
        BroadcastService broadcast,
        ExecutionService exec)
    {
        _dash = dash;
        _uow = uow;
        _me = me;
        _esc = esc;
        _broadcast = broadcast;
        _exec = exec;
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

    /// <summary>
    /// Runs one manual escalation cycle (design §5.4). Manager/Admin only — no "own teams" scoping here,
    /// matching the brief's action bar (a TeamLeader never sees these buttons in the first place,
    /// <c>DashboardVm.CanAct</c>). Stashes a plain (non-localized, numeric) result summary in
    /// <c>TempData["toast"]</c> for the redirected-to dashboard to render.
    /// </summary>
    [HttpPost("{id:guid}/run-escalation")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunEscalation(Guid id, CancellationToken ct)
    {
        var r = await _esc.RunCycleAsync(id, ct);
        TempData["toast"] = $"+{r.AttemptsAdded}/{r.Inducted}";
        return Redirect($"/admin/activations/{id}");
    }

    /// <summary>Sends a broadcast to every participant (design §5.6, FR-BRD-1). Manager/Admin only.</summary>
    [HttpPost("{id:guid}/broadcast")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Broadcast(Guid id, string body, CancellationToken ct)
    {
        await _broadcast.BroadcastAsync(id, body, ct);
        return Redirect($"/admin/activations/{id}");
    }

    /// <summary>Closes the activation and redirects to its static post-close Summary. Manager/Admin only.</summary>
    [HttpPost("{id:guid}/close")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        await _exec.CloseAsync(id, ct);
        return Redirect($"/admin/activations/{id}/summary");
    }

    /// <summary>
    /// The static final-state view of a Closed activation (§16 "no live updates" — no action bar, no
    /// realtime script section). Still-Active activations bounce back to the live Dashboard. Manager/Admin
    /// only, same as the other new actions in this task.
    /// </summary>
    [HttpGet("{id:guid}/summary")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Summary(Guid id, CancellationToken ct)
    {
        var dto = await _dash.GetSnapshotAsync(id, ct);
        if (dto.Status != ActivationStatus.Closed)
        {
            return Redirect($"/admin/activations/{id}");
        }

        return View(new DashboardVm(dto, false));
    }
}
