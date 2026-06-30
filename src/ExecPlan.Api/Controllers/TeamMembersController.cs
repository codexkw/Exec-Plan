using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="TeamMembership"/> (route <c>api/v1/team-members</c>; PRD §14: reads and
/// writes are Manager+Admin via <see cref="AuthPolicies.ManagerOrAdmin"/>).</summary>
[ApiController]
[Route("api/v1/team-members")]
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class TeamMembersController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public TeamMembersController(IUnitOfWork uow) => _uow = uow;

    private static TeamMemberDto ToDto(TeamMembership m) => new(m.Id, m.TeamId, m.UserId);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<TeamMembership>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var m = await _uow.Repo<TeamMembership>().GetByIdAsync(id, ct);
        return m is null ? NotFound() : Ok(ToDto(m));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamMemberDto dto, CancellationToken ct)
    {
        var m = new TeamMembership { TeamId = dto.TeamId, UserId = dto.UserId };
        await _uow.Repo<TeamMembership>().AddAsync(m, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(m));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTeamMemberDto dto, CancellationToken ct)
    {
        var m = await _uow.Repo<TeamMembership>().GetByIdAsync(id, ct);
        if (m is null)
        {
            return NotFound();
        }

        m.TeamId = dto.TeamId;
        m.UserId = dto.UserId;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(m));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var m = await _uow.Repo<TeamMembership>().GetByIdAsync(id, ct);
        if (m is null)
        {
            return NotFound();
        }

        _uow.Repo<TeamMembership>().Remove(m);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record TeamMemberDto(Guid Id, Guid TeamId, Guid UserId);

public sealed record CreateTeamMemberDto(Guid TeamId, Guid UserId);

public sealed record UpdateTeamMemberDto(Guid TeamId, Guid UserId);
