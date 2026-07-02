namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 10: create-plan wizard step 2 (teams &amp; members). Reused for both directions: the GET/POST
/// re-render shows already-persisted teams as a list of <see cref="TeamInput"/> (looked back up from the
/// DB by <c>PlanWizardController</c>), and the same shape is the model bound from the "add a team" form
/// post (name + optional leader + member ids). <see cref="AllUsers"/> feeds the leader/member pickers.
/// </summary>
public sealed class WizardTeamsVm
{
    public Guid PlanId { get; set; }
    public List<TeamInput> Teams { get; set; } = new();
    public IReadOnlyList<UserOption> AllUsers { get; set; } = Array.Empty<UserOption>();
}

public sealed class TeamInput
{
    // Populated on the read-back so the "remove team / remove member" forms can identify the row;
    // left default (Guid.Empty) on the "add a team" form post.
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? TeamLeaderUserId { get; set; }
    public List<Guid> MemberUserIds { get; set; } = new();
}

public sealed class UserOption
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
}
