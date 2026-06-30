using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="User"/> (PRD §14: reads = Admin+Manager via
/// <see cref="AuthPolicies.ManagerOrAdmin"/>, writes = Admin-only). Never returns
/// <see cref="User.PasswordHash"/> — every response is an explicit <see cref="UserDto"/> projection,
/// never the raw entity. <see cref="Update"/> only ever touches FullName/Phone/Role/DepartmentId/IsActive;
/// password reset is out of scope (handled separately, not via this endpoint).</summary>
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher _hasher;

    public UsersController(IUnitOfWork uow, IPasswordHasher hasher)
    {
        _uow = uow;
        _hasher = hasher;
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.UserName, u.FullName, u.Phone, u.Role, u.OrganizationId, u.DepartmentId, u.IsActive);

    [HttpGet, Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<User>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}"), Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var u = await _uow.Repo<User>().GetByIdAsync(id, ct);
        return u is null ? NotFound() : Ok(ToDto(u));
    }

    [HttpPost, Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create([FromBody] CreateUserDto dto, CancellationToken ct)
    {
        var u = new User
        {
            UserName = dto.UserName,
            PasswordHash = _hasher.Hash(dto.Password),
            FullName = dto.FullName,
            Phone = dto.Phone,
            Role = dto.Role,
            OrganizationId = dto.OrganizationId,
            DepartmentId = dto.DepartmentId,
        };
        await _uow.Repo<User>().AddAsync(u, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(u));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserDto dto, CancellationToken ct)
    {
        var u = await _uow.Repo<User>().GetByIdAsync(id, ct);
        if (u is null)
        {
            return NotFound();
        }

        u.FullName = dto.FullName;
        u.Phone = dto.Phone;
        u.Role = dto.Role;
        u.DepartmentId = dto.DepartmentId;
        u.IsActive = dto.IsActive;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(u));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var u = await _uow.Repo<User>().GetByIdAsync(id, ct);
        if (u is null)
        {
            return NotFound();
        }

        _uow.Repo<User>().Remove(u);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record UserDto(
    Guid Id, string UserName, string FullName, string Phone, UserRole Role, Guid OrganizationId, Guid? DepartmentId, bool IsActive);

public sealed record CreateUserDto(
    string UserName, string Password, string FullName, string Phone, UserRole Role, Guid OrganizationId, Guid? DepartmentId);

public sealed record UpdateUserDto(string FullName, string Phone, UserRole Role, Guid? DepartmentId, bool IsActive);
