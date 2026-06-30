using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="ShiftAssignment"/> (route <c>api/v1/shift-assignments</c>; PRD §14: reads
/// and writes are Manager+Admin via <see cref="AuthPolicies.ManagerOrAdmin"/>).</summary>
[ApiController]
[Route("api/v1/shift-assignments")]
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class ShiftAssignmentsController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public ShiftAssignmentsController(IUnitOfWork uow) => _uow = uow;

    private static ShiftAssignmentDto ToDto(ShiftAssignment s) =>
        new(s.Id, s.TeamId, s.UserId, s.Shift, s.Date, s.SubstituteForUserId);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<ShiftAssignment>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var s = await _uow.Repo<ShiftAssignment>().GetByIdAsync(id, ct);
        return s is null ? NotFound() : Ok(ToDto(s));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShiftAssignmentDto dto, CancellationToken ct)
    {
        var s = new ShiftAssignment
        {
            TeamId = dto.TeamId,
            UserId = dto.UserId,
            Shift = dto.Shift,
            Date = dto.Date,
            SubstituteForUserId = dto.SubstituteForUserId,
        };
        await _uow.Repo<ShiftAssignment>().AddAsync(s, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(s));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateShiftAssignmentDto dto, CancellationToken ct)
    {
        var s = await _uow.Repo<ShiftAssignment>().GetByIdAsync(id, ct);
        if (s is null)
        {
            return NotFound();
        }

        s.TeamId = dto.TeamId;
        s.UserId = dto.UserId;
        s.Shift = dto.Shift;
        s.Date = dto.Date;
        s.SubstituteForUserId = dto.SubstituteForUserId;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(s));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await _uow.Repo<ShiftAssignment>().GetByIdAsync(id, ct);
        if (s is null)
        {
            return NotFound();
        }

        _uow.Repo<ShiftAssignment>().Remove(s);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record ShiftAssignmentDto(Guid Id, Guid TeamId, Guid UserId, ShiftBand Shift, DateTime Date, Guid? SubstituteForUserId);

public sealed record CreateShiftAssignmentDto(Guid TeamId, Guid UserId, ShiftBand Shift, DateTime Date, Guid? SubstituteForUserId);

public sealed record UpdateShiftAssignmentDto(Guid TeamId, Guid UserId, ShiftBand Shift, DateTime Date, Guid? SubstituteForUserId);
