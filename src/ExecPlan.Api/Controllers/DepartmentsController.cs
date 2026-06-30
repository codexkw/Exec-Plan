using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="Department"/> (PRD §14: reads = Admin+Manager via
/// <see cref="AuthPolicies.ManagerOrAdmin"/>, writes = Admin-only).</summary>
[ApiController]
[Route("api/v1/departments")]
public sealed class DepartmentsController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public DepartmentsController(IUnitOfWork uow) => _uow = uow;

    private static DepartmentDto ToDto(Department d) => new(d.Id, d.Name, d.OrganizationId);

    [HttpGet, Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<Department>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}"), Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var d = await _uow.Repo<Department>().GetByIdAsync(id, ct);
        return d is null ? NotFound() : Ok(ToDto(d));
    }

    [HttpPost, Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create([FromBody] CreateDepartmentDto dto, CancellationToken ct)
    {
        var d = new Department { Name = dto.Name, OrganizationId = dto.OrganizationId };
        await _uow.Repo<Department>().AddAsync(d, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(d));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDepartmentDto dto, CancellationToken ct)
    {
        var d = await _uow.Repo<Department>().GetByIdAsync(id, ct);
        if (d is null)
        {
            return NotFound();
        }

        d.Name = dto.Name;
        d.OrganizationId = dto.OrganizationId;
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(d));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var d = await _uow.Repo<Department>().GetByIdAsync(id, ct);
        if (d is null)
        {
            return NotFound();
        }

        _uow.Repo<Department>().Remove(d);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record DepartmentDto(Guid Id, string Name, Guid OrganizationId);

public sealed record CreateDepartmentDto(string Name, Guid OrganizationId);

public sealed record UpdateDepartmentDto(string Name, Guid OrganizationId);
