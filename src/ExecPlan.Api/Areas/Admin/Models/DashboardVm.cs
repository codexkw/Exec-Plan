using ExecPlan.Application.Dashboard;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 14: wraps the pure-read <see cref="DashboardDto"/> with a view-only capability flag —
/// <see cref="CanAct"/> is true for SystemAdmin/PlanManager and gates the action bar (rendered
/// disabled/placeholder in this task; Task 15 wires the real run-escalation/broadcast/close POSTs). A
/// TeamLeader always renders with <see cref="CanAct"/>=false — they may view a dashboard for a team they
/// lead but never act on it (PRD §14).
/// </summary>
public sealed record DashboardVm(DashboardDto Dto, bool CanAct);

/// <summary>
/// The thin TeamLeader landing list (<c>GET /admin/activations</c>, design §7.9 — the target of
/// <c>HomeController.Index</c>'s TeamLeader redirect): every currently-Active
/// <see cref="ExecPlan.Domain.Entities.PlanActivation"/> with at least one participating team the caller
/// leads, each linking to its dashboard. SystemAdmin/PlanManager never see this view — they are
/// redirected to <c>/admin/plans</c> before it is built.
/// </summary>
public sealed class LeaderActivationsVm
{
    public IReadOnlyList<Row> Activations { get; init; } = Array.Empty<Row>();

    public sealed record Row(Guid Id, ShiftBand Shift, DateTime RosterDate);
}
