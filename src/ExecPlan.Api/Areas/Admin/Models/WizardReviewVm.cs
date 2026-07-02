using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 12: create-plan wizard step 4 (shifts &amp; review -&gt; Ready). Unlike <see cref="WizardTeamsVm"/>
/// / <see cref="WizardTasksVm"/> (which incrementally persist one "add" at a time across many posts), the
/// roster here is submitted and staged as a single batch on the one Finish post — so <see cref="Roster"/>
/// doubles as both the GET's pre-populated default (one row per existing <c>TeamMembership</c> of the
/// draft, built by <c>PlanWizardController.BuildReviewVmAsync</c>) and the model bound back from that
/// Finish form. <see cref="Readback"/> is a read-only projection of the whole plan for the review pane,
/// the same shape <see cref="PlanDetailVm"/> uses for the post-Ready read-only plan page.
/// </summary>
public sealed class WizardReviewVm
{
    public Guid PlanId { get; set; }
    public List<RosterInput> Roster { get; set; } = new();
    public ReviewReadback Readback { get; set; } = new();

    // The Kuwait shift + roster date resolved for "now" (KuwaitShiftCalculator.Resolve(IClock.UtcNow)) —
    // the exact band/date the activation cycle will match on-duty rows against. Surfaced so the roster
    // step can show a "current Kuwait shift" hint and a "set all rows to it" control, so a manager always
    // knows what to set for an immediate launch.
    public ShiftBand CurrentShift { get; set; }
    public DateTime CurrentDate { get; set; }
}

public sealed class RosterInput
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public ShiftBand Shift { get; set; }
    public DateTime Date { get; set; }
    public Guid? SubstituteForUserId { get; set; }
}

/// <summary>Read-only projection of the whole plan (info + teams/members/tasks) for the step 4 review pane.</summary>
public sealed class ReviewReadback
{
    public string PlanName { get; set; } = "";
    public PlanType Type { get; set; }
    public string? Objective { get; set; }
    public string? Description { get; set; }
    public string? Scope { get; set; }
    public IReadOnlyList<TeamBlock> Teams { get; set; } = Array.Empty<TeamBlock>();

    public sealed record TeamBlock(Guid TeamId, string Name, IReadOnlyList<MemberRow> Members, IReadOnlyList<string> Tasks);

    public sealed record MemberRow(Guid UserId, string Label);
}
