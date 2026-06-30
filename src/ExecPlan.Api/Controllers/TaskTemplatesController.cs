using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="TaskTemplate"/> (route <c>api/v1/task-templates</c>; PRD §14: reads and
/// writes are Manager+Admin via <see cref="AuthPolicies.ManagerOrAdmin"/>).</summary>
[ApiController]
[Route("api/v1/task-templates")]
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class TaskTemplatesController : ControllerBase
{
    private readonly IUnitOfWork _uow;

    public TaskTemplatesController(IUnitOfWork uow) => _uow = uow;

    private static TaskTemplateDto ToDto(TaskTemplate t) => new(t.Id, t.TeamId, t.Title, t.Order, t.Duration);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<TaskTemplate>().ListAsync(ct: ct)).Select(ToDto));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var t = await _uow.Repo<TaskTemplate>().GetByIdAsync(id, ct);
        return t is null ? NotFound() : Ok(ToDto(t));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTaskTemplateDto dto, CancellationToken ct)
    {
        var t = new TaskTemplate { TeamId = dto.TeamId, Title = dto.Title, Order = dto.Order, Duration = dto.Duration };
        await _uow.Repo<TaskTemplate>().AddAsync(t, ct);
        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(t));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTaskTemplateDto dto, CancellationToken ct)
    {
        var t = await _uow.Repo<TaskTemplate>().GetByIdAsync(id, ct);
        if (t is null)
        {
            return NotFound();
        }

        t.TeamId = dto.TeamId;
        t.Title = dto.Title;
        t.Order = dto.Order;
        t.Duration = dto.Duration;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(t));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await _uow.Repo<TaskTemplate>().GetByIdAsync(id, ct);
        if (t is null)
        {
            return NotFound();
        }

        _uow.Repo<TaskTemplate>().Remove(t);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record TaskTemplateDto(Guid Id, Guid TeamId, string Title, int Order, TimeSpan Duration);

public sealed record CreateTaskTemplateDto(Guid TeamId, string Title, int Order, TimeSpan Duration);

public sealed record UpdateTaskTemplateDto(Guid TeamId, string Title, int Order, TimeSpan Duration);
