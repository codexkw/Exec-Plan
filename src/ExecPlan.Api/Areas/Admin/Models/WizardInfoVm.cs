using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// Task 9: create-plan wizard step 1 (plan info). Posting this creates the <c>Plan</c> Draft row that
/// the rest of the wizard (Tasks 10-12: teams/tasks/review) resumes against by id.
/// </summary>
public sealed class WizardInfoVm
{
    public string Name { get; set; } = "";
    public PlanType Type { get; set; }
    public string? Objective { get; set; }
    public string? Description { get; set; }
    public string? Scope { get; set; }
}
