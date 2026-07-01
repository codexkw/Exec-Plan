using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 8: read-only plan detail. <see cref="Teams"/> is assembled in the controller from separate
/// <c>IRepository&lt;T&gt;.ListAsync</c> calls over Team/TeamMembership/User/TaskTemplate (no EF
/// <c>.Include()</c> — kept inside the Application repository abstraction). <see cref="ActiveActivationId"/>
/// mirrors <see cref="PlanListVm.Row.ActiveActivationId"/> and drives the same "watch dashboard" link;
/// the Activate action/button itself is Task 13 (the view leaves a marked placeholder region for it).
/// </summary>
public sealed class PlanDetailVm
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public PlanType Type { get; init; }
    public PlanStatus Status { get; init; }
    public string? Objective { get; init; }
    public string? Description { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyList<TeamBlock> Teams { get; init; } = Array.Empty<TeamBlock>();
    public Guid? ActiveActivationId { get; init; }

    public sealed record TeamBlock(string Name, IReadOnlyList<string> Members, IReadOnlyList<string> Tasks);
}
