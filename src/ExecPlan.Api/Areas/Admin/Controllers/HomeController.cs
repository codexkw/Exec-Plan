using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Role landing page for <c>/admin</c>. SystemAdmin/PlanManager get the operational <b>dashboard</b>
/// (KPI tiles + readiness pulse + recent activations); a TeamLeader is redirected to their own activation
/// landing (<c>/admin/activations</c>); anyone else back to login. The dashboard is assembled in-process
/// from <see cref="IUnitOfWork"/> (never admin → HTTP → API): <c>CountAsync</c> for the pure counts, a
/// filtered <c>ListAsync(Active)</c> for the readiness pulse (bounded by the number of currently-Active
/// activations, not the full history), and a server-side ordered/limited <c>ListRecentAsync</c> for the
/// recent list — so no query ever materializes the ever-growing <see cref="PlanActivation"/> table.
/// </summary>
[Area("Admin")]
[Route("admin")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
public sealed class HomeController : Controller
{
    private const int RecentCount = 6;

    private readonly IUnitOfWork _uow;

    public HomeController(IUnitOfWork uow) => _uow = uow;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var isManagerOrAdmin =
            User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager));
        if (!isManagerOrAdmin)
        {
            return User.IsInRole(nameof(UserRole.TeamLeader))
                ? Redirect("/admin/activations") // thin leader landing (list of activations they lead)
                : Redirect("/admin/login");
        }

        var plans = _uow.Repo<Plan>();
        var plansTotal = await plans.CountAsync(ct: ct);
        var plansReady = await plans.CountAsync(p => p.Status == PlanStatus.Ready, ct);

        var usersTotal = await _uow.Repo<User>().CountAsync(ct: ct);
        var departmentsTotal = await _uow.Repo<Department>().CountAsync(ct: ct);
        var organizationsTotal = await _uow.Repo<Organization>().CountAsync(ct: ct);
        var teamsTotal = await _uow.Repo<Team>().CountAsync(ct: ct);

        // Readiness pulse: only the currently-Active activations (a small, bounded working set), plus their
        // participants. Recent list: the top-N most recently activated, ordered + limited server-side.
        // Neither materializes the full (ever-growing) PlanActivation history.
        var activeActivations = await _uow.Repo<PlanActivation>()
            .ListAsync(a => a.Status == ActivationStatus.Active, ct);
        var activeIds = activeActivations.Select(a => a.Id).ToHashSet();

        var participants = activeIds.Count == 0
            ? new List<ActivationParticipant>()
            : await _uow.Repo<ActivationParticipant>().ListAsync(p => activeIds.Contains(p.ActivationId), ct);

        var recent = await _uow.Repo<PlanActivation>()
            .ListRecentAsync(a => a.ActivatedAtUtc, RecentCount, null, ct);
        var recentPlanIds = recent.Select(a => a.PlanId).Distinct().ToList();
        var recentPlans = recentPlanIds.Count == 0
            ? new List<Plan>()
            : await _uow.Repo<Plan>().ListAsync(p => recentPlanIds.Contains(p.Id), ct);

        int byStatus(ParticipantStatus s) => participants.Count(p => p.Status == s);

        var vm = new HomeDashboardVm
        {
            DisplayName = User.Identity?.Name,
            PlansTotal = plansTotal,
            PlansReady = plansReady,
            PlansDraft = plansTotal - plansReady,
            ActiveActivations = activeActivations.Count,
            UsersTotal = usersTotal,
            DepartmentsTotal = departmentsTotal,
            OrganizationsTotal = organizationsTotal,
            TeamsTotal = teamsTotal,
            Pending = byStatus(ParticipantStatus.Pending),
            Ready = byStatus(ParticipantStatus.Ready),
            Escalated = byStatus(ParticipantStatus.Escalated),
            Inducted = byStatus(ParticipantStatus.Inducted),
            Recent = recent
                .Select(a => new HomeDashboardVm.Row(
                    a.Id,
                    recentPlans.FirstOrDefault(p => p.Id == a.PlanId)?.Name ?? "—",
                    a.Shift,
                    a.Status,
                    a.ActivatedAtUtc))
                .ToList(),
        };

        return View(vm);
    }
}
