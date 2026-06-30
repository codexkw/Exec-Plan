using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>CRUD over <see cref="Plan"/> plus its nested <see cref="PlanContact"/>/<see cref="PlanActivator"/>
/// children (PRD §14: plan authoring is Manager+Admin for both reads and writes via
/// <see cref="AuthPolicies.ManagerOrAdmin"/>). <see cref="Create"/> sets <see cref="Plan.CreatedByUserId"/>
/// from <see cref="ICurrentUser"/> and stages any nested Contacts/Activators via
/// <c>Repo&lt;TChild&gt;().AddAsync</c> with the ctor-assigned parent <see cref="Plan.Id"/> as their FK —
/// never by mutating a tracked parent's nav collection (CLAUDE.md convention 2).</summary>
[ApiController]
[Route("api/v1/plans")]
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class PlansController : ControllerBase
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _currentUser;

    public PlansController(IUnitOfWork uow, ICurrentUser currentUser)
    {
        _uow = uow;
        _currentUser = currentUser;
    }

    private static PlanDto ToDto(Plan p, List<PlanContactDto>? contacts = null, List<PlanActivatorDto>? activators = null) =>
        new(p.Id, p.Name, p.Type, p.Objective, p.Description, p.Scope, p.Status, p.CreatedByUserId,
            contacts ?? new List<PlanContactDto>(), activators ?? new List<PlanActivatorDto>());

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await _uow.Repo<Plan>().ListAsync(ct: ct)).Select(p => ToDto(p)));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await _uow.Repo<Plan>().GetByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        var contacts = await _uow.Repo<PlanContact>().ListAsync(c => c.PlanId == id, ct);
        var activators = await _uow.Repo<PlanActivator>().ListAsync(a => a.PlanId == id, ct);

        return Ok(ToDto(
            p,
            contacts.Select(c => new PlanContactDto(c.Id, c.Name, c.Number, c.Kind)).ToList(),
            activators.Select(a => new PlanActivatorDto(a.Id, a.UserId)).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePlanDto dto, CancellationToken ct)
    {
        if (_currentUser.UserId is not Guid userId)
        {
            return Forbid();
        }

        var plan = new Plan
        {
            Name = dto.Name,
            Type = dto.Type,
            Objective = dto.Objective,
            Description = dto.Description,
            Scope = dto.Scope,
            CreatedByUserId = userId,
        };
        await _uow.Repo<Plan>().AddAsync(plan, ct);

        var contactDtos = new List<PlanContactDto>();
        foreach (var c in dto.Contacts ?? Enumerable.Empty<CreatePlanContactDto>())
        {
            var contact = new PlanContact { PlanId = plan.Id, Name = c.Name, Number = c.Number, Kind = c.Kind };
            await _uow.Repo<PlanContact>().AddAsync(contact, ct);
            contactDtos.Add(new PlanContactDto(contact.Id, contact.Name, contact.Number, contact.Kind));
        }

        var activatorDtos = new List<PlanActivatorDto>();
        foreach (var a in dto.Activators ?? Enumerable.Empty<CreatePlanActivatorDto>())
        {
            var activator = new PlanActivator { PlanId = plan.Id, UserId = a.UserId };
            await _uow.Repo<PlanActivator>().AddAsync(activator, ct);
            activatorDtos.Add(new PlanActivatorDto(activator.Id, activator.UserId));
        }

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(plan, contactDtos, activatorDtos));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePlanDto dto, CancellationToken ct)
    {
        var p = await _uow.Repo<Plan>().GetByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        p.Name = dto.Name;
        p.Type = dto.Type;
        p.Objective = dto.Objective;
        p.Description = dto.Description;
        p.Scope = dto.Scope;

        await _uow.SaveChangesAsync(ct);
        return Ok(ToDto(p));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await _uow.Repo<Plan>().GetByIdAsync(id, ct);
        if (p is null)
        {
            return NotFound();
        }

        _uow.Repo<Plan>().Remove(p);
        await _uow.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record PlanContactDto(Guid Id, string Name, string Number, ContactKind Kind);

public sealed record PlanActivatorDto(Guid Id, Guid UserId);

public sealed record PlanDto(
    Guid Id, string Name, PlanType Type, string Objective, string Description, string Scope, PlanStatus Status,
    Guid CreatedByUserId, List<PlanContactDto> Contacts, List<PlanActivatorDto> Activators);

public sealed record CreatePlanContactDto(string Name, string Number, ContactKind Kind);

public sealed record CreatePlanActivatorDto(Guid UserId);

public sealed record CreatePlanDto(
    string Name, PlanType Type, string Objective, string Description, string Scope,
    List<CreatePlanContactDto>? Contacts, List<CreatePlanActivatorDto>? Activators);

public sealed record UpdatePlanDto(string Name, PlanType Type, string Objective, string Description, string Scope);
