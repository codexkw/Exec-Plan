using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Localization;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Departments administration for the MVC admin area. Class gate is <see cref="AuthPolicies.ManagerOrAdmin"/>
/// (Manager gets a read-only <see cref="Index"/> — <see cref="DeptListVm.CanWrite"/> flags this so the
/// view can hide the Add link); <see cref="Create(CancellationToken)"/> (GET and POST) additionally
/// requires <see cref="AuthPolicies.Admin"/>. The create form needs an Organization <c>&lt;select&gt;</c>
/// (<see cref="DeptVm.Orgs"/>). Each write action ends in exactly one
/// <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
[Area("Admin")]
[Route("admin/departments")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class DepartmentsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IStringLocalizer<Resources.SharedResource> _localizer;

    public DepartmentsController(IUnitOfWork uow, IStringLocalizer<Resources.SharedResource> localizer)
    {
        _uow = uow;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var depts = await _uow.Repo<Department>().ListAsync(null, ct);
        var orgs = await _uow.Repo<Organization>().ListAsync(null, ct);

        var vm = new DeptListVm
        {
            CanWrite = User.IsInRole(nameof(UserRole.SystemAdmin)),
            Departments = depts
                .Select(d => new DeptListVm.Row(d.Id, d.Name, orgs.FirstOrDefault(o => o.Id == d.OrganizationId)?.Name))
                .ToList(),
        };
        return View(vm);
    }

    [HttpGet("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create(CancellationToken ct) => View(await BuildVmAsync(new DeptVm(), ct));

    [HttpPost("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(DeptVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Name))
        {
            ModelState.AddModelError("", _localizer["Validation.NameRequired"].Value);
        }

        if (!ModelState.IsValid)
        {
            return View(await BuildVmAsync(vm, ct));
        }

        var dept = new Department { Name = vm.Name!, OrganizationId = vm.OrganizationId };
        await _uow.Repo<Department>().AddAsync(dept, ct);
        await _uow.SaveChangesAsync(ct);
        return Redirect("/admin/departments");
    }

    private async Task<DeptVm> BuildVmAsync(DeptVm vm, CancellationToken ct)
    {
        var orgs = await _uow.Repo<Organization>().ListAsync(null, ct);
        vm.Orgs = new SelectList(orgs, nameof(Organization.Id), nameof(Organization.Name), vm.OrganizationId);
        return vm;
    }
}
