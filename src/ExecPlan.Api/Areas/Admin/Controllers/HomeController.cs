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
/// from <see cref="IUnitOfWork"/> (never admin → HTTP → API), using <c>CountAsync</c> for the pure counts
/// and the codebase's accepted "load Active + aggregate in memory" pattern (see PlansController.Index) for
/// the readiness pulse and the recent list — both bounded to small working sets at this scale.
/// </summary>
[Area("Admin")]
[Route("admin")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
public sealed class HomeController : Controller
{
    private const int RecentCount = 6;

    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _me;

    public HomeController(IUnitOfWork uow, ICurrentUser me)
    {
        _uow = uow;
        _me = me;
    }

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

        // One read of the activations table; derive both the active set (for the readiness pulse) and the
        // recent list from it, so this touches the table once.
        var activations = await _uow.Repo<PlanActivation>().ListAsync(null, ct);
        var activeActivations = activations.Where(a => a.Status == ActivationStatus.Active).ToList();
        var activeIds = activeActivations.Select(a => a.Id).ToHashSet();

        var participants = activeIds.Count == 0
            ? new List<ActivationParticipant>()
            : await _uow.Repo<ActivationParticipant>().ListAsync(p => activeIds.Contains(p.ActivationId), ct);

        var recent = activations
            .OrderByDescending(a => a.ActivatedAtUtc)
            .Take(RecentCount)
            .ToList();
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
