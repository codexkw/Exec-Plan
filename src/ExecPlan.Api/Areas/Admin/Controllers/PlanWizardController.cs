using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Server-incremental create-plan wizard for the MVC admin area. Each step commits straight to the DB
/// against a <see cref="Plan"/> that starts life as a <see cref="PlanStatus.Draft"/> — so the wizard is
/// resumable/refresh-safe across steps instead of holding unsaved state in the browser. This task (9)
/// wires only <see cref="Info"/> (step 1: plan info -> Draft); Tasks 10-12 add teams/tasks/review onto
/// the same <c>{id}</c>. Class gate is <see cref="AuthPolicies.ManagerOrAdmin"/>.
/// </summary>
[Area("Admin")]
[Route("admin/plans/create")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class PlanWizardController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _me;

    public PlanWizardController(IUnitOfWork uow, ICurrentUser me)
    {
        _uow = uow;
        _me = me;
    }

    [HttpGet("")]
    public IActionResult Info()
    {
        ViewData["step"] = 1;
        return View(new WizardInfoVm());
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Info(WizardInfoVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Name))
        {
            ModelState.AddModelError(nameof(vm.Name), "required");
        }

        if (!ModelState.IsValid)
        {
            ViewData["step"] = 1;
            return View(vm);
        }

        var plan = new Plan
        {
            Name = vm.Name,
            Type = vm.Type,
            Objective = vm.Objective ?? "",
            Description = vm.Description ?? "",
            Scope = vm.Scope ?? "",
            Status = PlanStatus.Draft,
            CreatedByUserId = _me.UserId!.Value,
        };

        // The creator is an implicit authorized activator.
        await _uow.Repo<Plan>().AddAsync(plan, ct);
        await _uow.Repo<PlanActivator>().AddAsync(new PlanActivator { PlanId = plan.Id, UserId = _me.UserId!.Value }, ct);

        await _uow.SaveChangesAsync(ct);
        return Redirect($"/admin/plans/create/{plan.Id}/teams");
    }
}
