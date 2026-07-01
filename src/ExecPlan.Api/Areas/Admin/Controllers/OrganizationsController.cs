using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Organizations administration for the MVC admin area (satisfies FR-ADM-1 — PRD §16's screen list
/// omitted it, per DEC-25). Class gate is <see cref="AuthPolicies.ManagerOrAdmin"/> (Manager gets a
/// read-only <see cref="Index"/> — <see cref="OrgListVm.CanWrite"/> flags this so the view can hide the
/// Add link); <see cref="Create(CancellationToken)"/> (GET and POST) additionally requires
/// <see cref="AuthPolicies.Admin"/>. Each write action ends in exactly one
/// <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
[Area("Admin")]
[Route("admin/organizations")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class OrganizationsController : Controller
{
    private readonly IUnitOfWork _uow;

    public OrganizationsController(IUnitOfWork uow)
    {
        _uow = uow;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var orgs = await _uow.Repo<Organization>().ListAsync(null, ct);

        var vm = new OrgListVm
        {
            CanWrite = User.IsInRole(nameof(UserRole.SystemAdmin)),
            Organizations = orgs.Select(o => new OrgListVm.Row(o.Id, o.Name)).ToList(),
        };
        return View(vm);
    }

    [HttpGet("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    public IActionResult Create() => View(new OrgVm());

    [HttpPost("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(OrgVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Name))
        {
            ModelState.AddModelError("", "Organizations required fields");
        }

        if (!ModelState.IsValid)
        {
            return View(vm);
        }

        var org = new Organization { Name = vm.Name! };
        await _uow.Repo<Organization>().AddAsync(org, ct);
        await _uow.SaveChangesAsync(ct);
        return Redirect("/admin/organizations");
    }
}
