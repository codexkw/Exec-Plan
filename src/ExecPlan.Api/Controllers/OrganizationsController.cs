using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="Organization"/> (PRD §14: reads = Admin+Manager via
/// <see cref="AuthPolicies.ManagerOrAdmin"/>, writes = Admin-only).</summary>
[ApiController]
[Route("api/v1/organizations")]
public sealed class OrganizationsController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public OrganizationsController(IUnitOfWork uow) => _uow = uow;

    private static OrganizationDto ToDto(Organization o) => new(o.Id, o.Name);

    [HttpGet, Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<Organization>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}"), Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var o = await _uow.Repo<Organization>().GetByIdAsync(id, ct);
        return o is null ? NotFound() : Ok(ToDto(o));
    }

    [HttpPost, Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Create([FromBody] CreateOrganizationDto dto, CancellationToken ct)
    {
        var o = new Organization { Name = dto.Name };
        await _uow.Repo<Organization>().AddAsync(o, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(o));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateOrganizationDto dto, CancellationToken ct)
    {
        var o = await _uow.Repo<Organization>().GetByIdAsync(id, ct);
        if (o is null)
        {
            return NotFound();
        }

        o.Name = dto.Name;
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(o));
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var o = await _uow.Repo<Organization>().GetByIdAsync(id, ct);
        if (o is null)
        {
            return NotFound();
        }

        _uow.Repo<Organization>().Remove(o);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record OrganizationDto(Guid Id, string Name);

public sealed record CreateOrganizationDto(string Name);

public sealed record UpdateOrganizationDto(string Name);
