using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 8: "My Plans" list. <see cref="Row.ActiveActivationId"/> is populated only when a
/// <see cref="ExecPlan.Domain.Entities.PlanActivation"/> for that plan currently has
/// <see cref="ActivationStatus.Active"/> — the view uses it to render a "watch dashboard" link to
/// <c>/admin/activations/{id}</c> (spec §7.4).
/// </summary>
public sealed class PlanListVm
{
    public IReadOnlyList<Row> Plans { get; init; } = Array.Empty<Row>();

    public sealed record Row(Guid Id, string Name, PlanType Type, PlanStatus Status, Guid? ActiveActivationId);
}
