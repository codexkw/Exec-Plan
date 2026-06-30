using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="Team"/> (PRD §14: reads and writes are Manager+Admin via
/// <see cref="AuthPolicies.ManagerOrAdmin"/>).</summary>
[ApiController]
[Route("api/v1/teams")]
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class TeamsController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public TeamsController(IUnitOfWork uow) => _uow = uow;

    private static TeamDto ToDto(Team t) => new(t.Id, t.PlanId, t.Name, t.TeamLeaderUserId);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<Team>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var t = await _uow.Repo<Team>().GetByIdAsync(id, ct);
        return t is null ? NotFound() : Ok(ToDto(t));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTeamDto dto, CancellationToken ct)
    {
        var t = new Team { PlanId = dto.PlanId, Name = dto.Name, TeamLeaderUserId = dto.TeamLeaderUserId };
        await _uow.Repo<Team>().AddAsync(t, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(t));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTeamDto dto, CancellationToken ct)
    {
        var t = await _uow.Repo<Team>().GetByIdAsync(id, ct);
        if (t is null)
        {
            return NotFound();
        }

        t.PlanId = dto.PlanId;
        t.Name = dto.Name;
        t.TeamLeaderUserId = dto.TeamLeaderUserId;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(t));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await _uow.Repo<Team>().GetByIdAsync(id, ct);
        if (t is null)
        {
            return NotFound();
        }

        _uow.Repo<Team>().Remove(t);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record TeamDto(Guid Id, Guid PlanId, string Name, Guid? TeamLeaderUserId);

public sealed record CreateTeamDto(Guid PlanId, string Name, Guid? TeamLeaderUserId);

public sealed record UpdateTeamDto(Guid PlanId, string Name, Guid? TeamLeaderUserId);
