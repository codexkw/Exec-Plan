using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// "My Plans" list + read-only plan detail + Activate for the MVC admin area (Tasks 8, 13). Class gate
/// is <see cref="AuthPolicies.ManagerOrAdmin"/>: <see cref="Index"/> scopes the list to
/// <see cref="Plan.CreatedByUserId"/> == <see cref="ICurrentUser.UserId"/> for a manager, or every plan
/// for a <see cref="UserRole.SystemAdmin"/>. <see cref="Detail"/> assembles its team/member/task
/// read-back from separate <c>ListAsync</c> calls per collection (no EF <c>.Include()</c> here — that
/// stays inside the Application repository abstraction) and throws <see cref="AppException.NotFound"/>
/// for an unknown id, which <c>AppExceptionMiddleware</c> turns into a redirect to the shared NotFound
/// view. The Create-Plan wizard is Tasks 9-12 (<c>PlanWizardController</c>).
/// <see cref="Activate"/> (Task 13) delegates to <see cref="IActivationService.ActivateAsync"/> in
/// process (never HTTP) and does NOT catch <see cref="AppException.Kind.NotFound"/>/
/// <see cref="AppException.Kind.Forbidden"/> — those propagate to <c>AppExceptionMiddleware</c>, which
/// redirects to <c>/admin/notfound</c>/<c>/admin/denied</c> respectively. Only
/// <see cref="AppException.Kind.Conflict"/> (the common "no one on duty"/"already active" case) and,
/// defensively, <see cref="AppException.Kind.Validation"/> are caught here and turned into a redirect
/// back to Detail with a <c>TempData["activateError"]</c> message the view renders as an alert.
/// </summary>
[Area("Admin")]
[Route("admin/plans")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class PlansController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly IActivationService _activation;

    public PlansController(IUnitOfWork uow, ICurrentUser currentUser, IActivationService activation)
    {
        _uow = uow;
        _currentUser = currentUser;
        _activation = activation;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var isAdmin = User.IsInRole(nameof(UserRole.SystemAdmin));
        var userId = _currentUser.UserId ?? Guid.Empty;

        var plans = isAdmin
            ? await _uow.Repo<Plan>().ListAsync(null, ct)
            : await _uow.Repo<Plan>().ListAsync(p => p.CreatedByUserId == userId, ct);

        var activeActivations = await _uow.Repo<PlanActivation>()
            .ListAsync(a => a.Status == ActivationStatus.Active, ct);

        var vm = new PlanListVm
        {
            Plans = plans
                .Select(p => new PlanListVm.Row(
                    p.Id,
                    p.Name,
                    p.Type,
                    p.Status,
                    activeActivations.FirstOrDefault(a => a.PlanId == p.Id)?.Id))
                .ToList(),
        };
        return View(vm);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, CancellationToken ct)
    {
        var plan = await _uow.Repo<Plan>().GetByIdAsync(id, ct);
        if (plan is null)
        {
            throw AppException.NotFound($"Plan {id} was not found.");
        }

        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == id, ct);
        var teamIds = teams.Select(t => t.Id).ToList();

        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);
        var userIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = await _uow.Repo<User>().ListAsync(u => userIds.Contains(u.Id), ct);

        var tasks = await _uow.Repo<TaskTemplate>().ListAsync(t => teamIds.Contains(t.TeamId), ct);

        var activeActivation = await _uow.Repo<PlanActivation>()
            .FirstOrDefaultAsync(a => a.PlanId == id && a.Status == ActivationStatus.Active, ct);

        var teamBlocks = teams
            .OrderBy(t => t.Name)
            .Select(t => new PlanDetailVm.TeamBlock(
                t.Name,
                memberships
                    .Where(m => m.TeamId == t.Id)
                    .Select(m => users.FirstOrDefault(u => u.Id == m.UserId)?.FullName ?? "")
                    .ToList(),
                tasks
                    .Where(tt => tt.TeamId == t.Id)
                    .OrderBy(tt => tt.Order)
                    .Select(tt => tt.Title)
                    .ToList()))
            .ToList();

        var vm = new PlanDetailVm
        {
            Id = plan.Id,
            Name = plan.Name,
            Type = plan.Type,
            Status = plan.Status,
            Objective = plan.Objective,
            Description = plan.Description,
            Scope = plan.Scope,
            Teams = teamBlocks,
            ActiveActivationId = activeActivation?.Id,
        };
        return View(vm);
    }

    [HttpPost("{id:guid}/activate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        try
        {
            var activationId = await _activation.ActivateAsync(id, _currentUser.UserId!.Value, ct);
            return Redirect($"/admin/activations/{activationId}");
        }
        catch (AppException ex) when (ex.ErrorKind is AppException.Kind.Conflict or AppException.Kind.Validation)
        {
            TempData["activateError"] = ex.Message;
            return Redirect($"/admin/plans/{id}");
        }
    }
}
