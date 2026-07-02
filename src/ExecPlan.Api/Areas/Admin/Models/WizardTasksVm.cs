namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 11: create-plan wizard step 3 (tasks). Reused for both directions, same convention as
/// <see cref="WizardTeamsVm"/>: the GET/POST re-render groups already-persisted <c>TaskTemplate</c>s
/// (looked back up from the DB by <c>PlanWizardController</c>) under each of the draft's teams as
/// <see cref="TeamTasks"/>, and each team's add-task form on the view binds a fresh <see cref="TaskInput"/>.
/// </summary>
public sealed class WizardTasksVm
{
    public Guid PlanId { get; set; }
    public IReadOnlyList<TeamTasks> Teams { get; set; } = Array.Empty<TeamTasks>();
}

public sealed class TeamTasks
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = "";
    public List<TaskInput> Tasks { get; set; } = new();
}

public sealed class TaskInput
{
    // Populated on the read-back so the per-task "remove" form can identify the row; left default
    // (Guid.Empty) on the "add a task" form post.
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public int DurationMinutes { get; set; }
}
