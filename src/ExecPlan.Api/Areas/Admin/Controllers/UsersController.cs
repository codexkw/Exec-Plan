using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Users administration for the MVC admin area. Class gate is <see cref="AuthPolicies.ManagerOrAdmin"/>
/// (Manager gets a read-only <see cref="Index"/> — <see cref="UserListVm.CanWrite"/> flags this so the
/// view can hide Add/Edit); <see cref="Create(CancellationToken)"/>/<see cref="Edit(Guid,CancellationToken)"/>
/// (GET and POST) additionally require <see cref="AuthPolicies.Admin"/>. Deactivating a user never
/// deletes the row — <see cref="Edit(Guid,UserEditVm,CancellationToken)"/> just flips
/// <see cref="User.IsActive"/> to false. Each write action ends in exactly one
/// <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
[Area("Admin")]
[Route("admin/users")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class UsersController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;

    public UsersController(IUnitOfWork uow, IPasswordHasher hasher)
    {
        _uow = uow;
        _hasher = hasher;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var users = await _uow.Repo<User>().ListAsync(null, ct);
        var depts = await _uow.Repo<Department>().ListAsync(null, ct);

        var vm = new UserListVm
        {
            CanWrite = User.IsInRole(nameof(UserRole.SystemAdmin)),
            Users = users
                .Select(u => new UserListVm.Row(u.Id, u.UserName, u.FullName, u.Role,
                    depts.FirstOrDefault(d => d.Id == u.DepartmentId)?.Name, u.IsActive))
                .ToList(),
        };
        return View(vm);
    }

    [HttpGet("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create(CancellationToken ct) => View(await BuildEditVmAsync(null, ct));

    [HttpPost("create")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(UserEditVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.UserName) || string.IsNullOrWhiteSpace(vm.Password))
        {
            ModelState.AddModelError("", "Users required fields");
        }

        if (!ModelState.IsValid)
        {
            await FillListsAsync(vm, ct);
            return View(vm);
        }

        var user = new User
        {
            UserName = vm.UserName!,
            FullName = vm.FullName ?? "",
            Phone = vm.Phone ?? "",
            Role = vm.Role,
            OrganizationId = vm.OrganizationId,
            DepartmentId = vm.DepartmentId,
            PasswordHash = _hasher.Hash(vm.Password!),
            IsActive = true,
        };
        await _uow.Repo<User>().AddAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
        return Redirect("/admin/users");
    }

    [HttpGet("{id:guid}/edit")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var user = await _uow.Repo<User>().GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound();
        }

        return View(await BuildEditVmAsync(user, ct));
    }

    [HttpPost("{id:guid}/edit")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.Admin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, UserEditVm vm, CancellationToken ct)
    {
        var user = await _uow.Repo<User>().GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(vm.UserName))
        {
            ModelState.AddModelError("", "Users required fields");
        }

        if (!ModelState.IsValid)
        {
            vm.Id = id;
            await FillListsAsync(vm, ct);
            return View(vm);
        }

        user.UserName = vm.UserName!;
        user.FullName = vm.FullName ?? "";
        user.Phone = vm.Phone ?? "";
        user.Role = vm.Role;
        user.OrganizationId = vm.OrganizationId;
        user.DepartmentId = vm.DepartmentId;
        user.IsActive = vm.IsActive; // deactivate = false here; the row is never deleted
        if (!string.IsNullOrWhiteSpace(vm.Password))
        {
            user.PasswordHash = _hasher.Hash(vm.Password);
        }

        await _uow.SaveChangesAsync(ct);
        return Redirect("/admin/users");
    }

    private async Task<UserEditVm> BuildEditVmAsync(User? user, CancellationToken ct)
    {
        var vm = new UserEditVm
        {
            Id = user?.Id,
            UserName = user?.UserName,
            FullName = user?.FullName,
            Phone = user?.Phone,
            Role = user?.Role ?? UserRole.TeamMember,
            OrganizationId = user?.OrganizationId ?? Guid.Empty,
            DepartmentId = user?.DepartmentId,
            IsActive = user?.IsActive ?? true,
        };
        await FillListsAsync(vm, ct);
        return vm;
    }

    private async Task FillListsAsync(UserEditVm vm, CancellationToken ct)
    {
        var orgs = await _uow.Repo<Organization>().ListAsync(null, ct);
        var depts = await _uow.Repo<Department>().ListAsync(null, ct);
        vm.Orgs = new SelectList(orgs, nameof(Organization.Id), nameof(Organization.Name), vm.OrganizationId);
        vm.Depts = new SelectList(depts, nameof(Department.Id), nameof(Department.Name), vm.DepartmentId);
    }
}
