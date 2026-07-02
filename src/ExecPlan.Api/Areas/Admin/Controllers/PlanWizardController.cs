using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Server-incremental create-plan wizard for the MVC admin area. Each step commits straight to the DB
/// against a <see cref="Plan"/> that starts life as a <see cref="PlanStatus.Draft"/> — so the wizard is
/// resumable/refresh-safe across steps instead of holding unsaved state in the browser. Task 9 wired
/// <see cref="Info"/> (step 1: plan info -> Draft); Task 10 added <see cref="Teams(Guid, CancellationToken)"/>
/// (step 2: teams &amp; members); this task (11) adds <see cref="Tasks(Guid, CancellationToken)"/> (step 3:
/// task templates per team) onto the same <c>{id}</c>; Task 12 adds review. Class gate is
/// <see cref="AuthPolicies.ManagerOrAdmin"/>.
/// </summary>
[Area("Admin")]
[Route("admin/plans/create")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme, Policy = AuthPolicies.ManagerOrAdmin)]
public sealed class PlanWizardController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser _me;
    private readonly IStringLocalizer<Resources.SharedResource> _localizer;
    private readonly IClock _clock;
    private readonly KuwaitShiftCalculator _shiftCalc;

    public PlanWizardController(
        IUnitOfWork uow,
        ICurrentUser me,
        IStringLocalizer<Resources.SharedResource> localizer,
        IClock clock,
        KuwaitShiftCalculator shiftCalc)
    {
        _uow = uow;
        _me = me;
        _localizer = localizer;
        _clock = clock;
        _shiftCalc = shiftCalc;
    }

    /// <summary>
    /// Reusable ownership guard shared by every editable wizard step (info/teams/tasks/review), for BOTH
    /// the create flow (a fresh Draft) and the post-Ready EDIT flow. Loads the plan by id TRACKED (so a
    /// step can mutate it and rely on a later <c>SaveChangesAsync</c>), and enforces that only the plan's
    /// own creator (or a SystemAdmin) may edit it. Missing plan → <see cref="AppException"/> NotFound;
    /// wrong owner → Forbidden (both mapped by <c>AppExceptionMiddleware</c> to the shared NotFound/denied
    /// pages). Unlike the original Draft-only guard this replaced, a <see cref="PlanStatus.Ready"/> plan is
    /// NOT redirected away — editing a finalized plan is allowed, because its template is independent of any
    /// running activation's frozen snapshot, so template edits only affect FUTURE activations.
    /// </summary>
    private async Task<Plan> LoadPlanForEditAsync(Guid id, CancellationToken ct)
    {
        var plan = await _uow.Repo<Plan>().FirstOrDefaultTrackedAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            throw AppException.NotFound($"Plan {id} was not found.");
        }

        var isAdmin = User.IsInRole(nameof(UserRole.SystemAdmin));
        if (plan.CreatedByUserId != _me.UserId && !isAdmin)
        {
            throw AppException.Forbidden($"You do not own plan {id}.");
        }

        // Every action that loads an EXISTING plan for editing can navigate between all four steps, so
        // expose the id to _Steps.cshtml (which renders the step chips as links only when it is present)
        // and to Info.cshtml (which posts to the {id}/info edit route rather than the create route).
        ViewData["planId"] = plan.Id;
        return plan;
    }

    [HttpGet("")]
    public IActionResult Info()
    {
        ViewData["step"] = 1;
        return View(new WizardInfoVm());
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Info(WizardInfoVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.Name))
        {
            ModelState.AddModelError(nameof(vm.Name), _localizer["Validation.NameRequired"].Value);
        }

        if (!ModelState.IsValid)
        {
            ViewData["step"] = 1;
            return View(vm);
        }

        var plan = new Plan
        {
            Name = vm.Name,
            Type = vm.Type,
            Objective = vm.Objective ?? "",
            Description = vm.Description ?? "",
            Scope = vm.Scope ?? "",
            Status = PlanStatus.Draft,
            CreatedByUserId = _me.UserId!.Value,
        };

        // The creator is an implicit authorized activator.
        await _uow.Repo<Plan>().AddAsync(plan, ct);
        await _uow.Repo<PlanActivator>().AddAsync(new PlanActivator { PlanId = plan.Id, UserId = _me.UserId!.Value }, ct);

        await _uow.SaveChangesAsync(ct);
        return Redirect($"/admin/plans/create/{plan.Id}/teams");
    }

    /// <summary>
    /// Step 1 of the EDIT flow (as opposed to <see cref="Info(WizardInfoVm, CancellationToken)"/>, which
    /// CREATES a new Draft): re-renders the shared <c>Info.cshtml</c> pre-filled from an existing plan
    /// (Draft or Ready). <see cref="LoadPlanForEditAsync"/> enforces owner/admin and exposes the id so the
    /// form posts back to <c>{id}/info</c>.
    /// </summary>
    [HttpGet("{id:guid}/info")]
    public async Task<IActionResult> EditInfo(Guid id, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);
        ViewData["step"] = 1;
        return View("Info", new WizardInfoVm
        {
            Name = plan.Name,
            Type = plan.Type,
            Objective = plan.Objective,
            Description = plan.Description,
            Scope = plan.Scope,
        });
    }

    /// <summary>
    /// Saves step-1 edits back onto an existing plan. Same required-Name validation as create; updates the
    /// tracked plan's info fields in place (never touches Status/CreatedBy) and commits with one
    /// <see cref="IUnitOfWork.SaveChangesAsync"/>, then advances to step 2.
    /// </summary>
    [HttpPost("{id:guid}/info")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditInfo(Guid id, WizardInfoVm vm, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        if (string.IsNullOrWhiteSpace(vm.Name))
        {
            ModelState.AddModelError(nameof(vm.Name), _localizer["Validation.NameRequired"].Value);
        }

        if (!ModelState.IsValid)
        {
            ViewData["step"] = 1;
            return View("Info", vm);
        }

        plan.Name = vm.Name;
        plan.Type = vm.Type;
        plan.Objective = vm.Objective ?? "";
        plan.Description = vm.Description ?? "";
        plan.Scope = vm.Scope ?? "";
        await _uow.SaveChangesAsync(ct);
        return Redirect($"/admin/plans/create/{id}/teams");
    }

    /// <summary>
    /// Wizard step-navigation guard (Task 12): given the requested step number, checks that every
    /// EARLIER step's completion prerequisite already holds for this draft, and returns a redirect to the
    /// earliest unmet one's URL if not — so a manager can't deep-link straight to <c>/tasks</c> or
    /// <c>/review</c> on a draft that never got a team+member or a task. Returns null once every
    /// prerequisite up to <paramref name="step"/> holds, meaning the caller should render normally. Step
    /// 2's only prerequisite is "this is still a Draft", already enforced by
    /// <see cref="LoadPlanForEditAsync"/>, so requesting step 2 is a no-op here; the checks only start
    /// biting from step 3 onward, re-checking the exact same "team with a member" / "team with a task"
    /// rules the Teams/Tasks POST "next" intents already enforce server-side (so a crafted/replayed GET
    /// can't reach a step those intents would have blocked).
    /// </summary>
    private async Task<IActionResult?> RequireStep(Guid planId, int step, CancellationToken ct)
    {
        if (step < 3)
        {
            return null;
        }

        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);

        if (!teams.Any(t => memberships.Any(m => m.TeamId == t.Id)))
        {
            return Redirect($"/admin/plans/create/{planId}/teams");
        }

        if (step < 4)
        {
            return null;
        }

        var templates = await _uow.Repo<TaskTemplate>().ListAsync(t => teamIds.Contains(t.TeamId), ct);
        if (!teams.Any(t => templates.Any(tt => tt.TeamId == t.Id)))
        {
            return Redirect($"/admin/plans/create/{planId}/tasks");
        }

        return null;
    }

    [HttpGet("{id:guid}/teams")]
    public async Task<IActionResult> Teams(Guid id, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var stepGuard = await RequireStep(plan.Id, 2, ct);
        if (stepGuard is not null)
        {
            return stepGuard;
        }

        ViewData["step"] = 2;
        return View(await BuildTeamsVmAsync(plan.Id, ct));
    }

    /// <summary>
    /// Two submit intents share this one route (see <c>Teams.cshtml</c>'s two sibling forms):
    /// <c>intent=add</c> adds one <see cref="Team"/> (plus a <see cref="TeamMembership"/> row per
    /// selected member, each via <see cref="IRepository{T}.AddAsync"/> — never by mutating a tracked
    /// collection nav) in a single <see cref="IUnitOfWork.SaveChangesAsync"/>, then re-renders step 2
    /// with the new team now listed; posting "add" again layers on another team, so a draft can collect
    /// as many teams as needed across multiple posts. <c>intent=next</c> only advances to
    /// <c>/tasks</c> once the draft has at least one team with at least one member — server-side, so a
    /// crafted/replayed request can't skip it — otherwise it re-renders step 2 with an inline message.
    /// </summary>
    [HttpPost("{id:guid}/teams")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Teams(Guid id, string intent, TeamInput input, Guid teamId, Guid userId, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var planId = plan.Id;
        ViewData["step"] = 2;

        if (intent == "add")
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                ModelState.AddModelError(nameof(input.Name), _localizer["Validation.NameRequired"].Value);
                return View(await BuildTeamsVmAsync(planId, ct));
            }

            var team = new Team
            {
                PlanId = planId,
                Name = input.Name,
                TeamLeaderUserId = input.TeamLeaderUserId,
            };
            await _uow.Repo<Team>().AddAsync(team, ct);

            foreach (var memberUserId in (input.MemberUserIds ?? new List<Guid>()).Distinct())
            {
                await _uow.Repo<TeamMembership>().AddAsync(new TeamMembership { TeamId = team.Id, UserId = memberUserId }, ct);
            }

            await _uow.SaveChangesAsync(ct);
            return View(await BuildTeamsVmAsync(planId, ct));
        }

        if (intent == "remove-team")
        {
            // Re-check the team belongs to THIS plan (a crafted teamId can't delete a foreign plan's team),
            // then cascade-delete its memberships, task templates, and roster rows before the team itself.
            var team = await _uow.Repo<Team>().FirstOrDefaultTrackedAsync(t => t.Id == teamId && t.PlanId == planId, ct);
            if (team is null)
            {
                return NotFound();
            }

            foreach (var m in await _uow.Repo<TeamMembership>().ListTrackedAsync(m => m.TeamId == teamId, ct))
            {
                _uow.Repo<TeamMembership>().Remove(m);
            }
            foreach (var tt in await _uow.Repo<TaskTemplate>().ListTrackedAsync(t => t.TeamId == teamId, ct))
            {
                _uow.Repo<TaskTemplate>().Remove(tt);
            }
            foreach (var sa in await _uow.Repo<ShiftAssignment>().ListTrackedAsync(s => s.TeamId == teamId, ct))
            {
                _uow.Repo<ShiftAssignment>().Remove(sa);
            }
            _uow.Repo<Team>().Remove(team);

            await _uow.SaveChangesAsync(ct);
            return View(await BuildTeamsVmAsync(planId, ct));
        }

        if (intent == "remove-member")
        {
            // Re-check the team belongs to this plan, then drop the membership plus any roster row that
            // involves the user (as the on-duty person OR as someone else's substitute) so no orphan
            // ShiftAssignment survives to reference a user who is no longer on the team.
            var team = await _uow.Repo<Team>().FirstOrDefaultAsync(t => t.Id == teamId && t.PlanId == planId, ct);
            if (team is null)
            {
                return NotFound();
            }

            foreach (var m in await _uow.Repo<TeamMembership>().ListTrackedAsync(m => m.TeamId == teamId && m.UserId == userId, ct))
            {
                _uow.Repo<TeamMembership>().Remove(m);
            }
            foreach (var sa in await _uow.Repo<ShiftAssignment>().ListTrackedAsync(
                         s => s.TeamId == teamId && (s.UserId == userId || s.SubstituteForUserId == userId), ct))
            {
                _uow.Repo<ShiftAssignment>().Remove(sa);
            }

            await _uow.SaveChangesAsync(ct);
            return View(await BuildTeamsVmAsync(planId, ct));
        }

        // intent == "next": at least one team with at least one member must exist for this draft.
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);

        if (!teams.Any(t => memberships.Any(m => m.TeamId == t.Id)))
        {
            ViewData["teamsBlocked"] = _localizer["Teams.AtLeastOneRequired"].Value;
            return View(await BuildTeamsVmAsync(planId, ct));
        }

        return Redirect($"/admin/plans/create/{planId}/tasks");
    }

    /// <summary>Assembles <see cref="WizardTeamsVm"/> from the draft's current DB state (existing teams
    /// + their memberships, plus every user for the leader/member pickers).</summary>
    private async Task<WizardTeamsVm> BuildTeamsVmAsync(Guid planId, CancellationToken ct)
    {
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);
        var users = await _uow.Repo<User>().ListAsync(null, ct);

        return new WizardTeamsVm
        {
            PlanId = planId,
            AllUsers = users
                .OrderBy(u => u.FullName)
                .Select(u => new UserOption { Id = u.Id, Label = u.FullName })
                .ToList(),
            Teams = teams
                .OrderBy(t => t.Name)
                .Select(t => new TeamInput
                {
                    Id = t.Id,
                    Name = t.Name,
                    TeamLeaderUserId = t.TeamLeaderUserId,
                    MemberUserIds = memberships.Where(m => m.TeamId == t.Id).Select(m => m.UserId).ToList(),
                })
                .ToList(),
        };
    }

    [HttpGet("{id:guid}/tasks")]
    public async Task<IActionResult> Tasks(Guid id, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var stepGuard = await RequireStep(plan.Id, 3, ct);
        if (stepGuard is not null)
        {
            return stepGuard;
        }

        ViewData["step"] = 3;
        return View(await BuildTasksVmAsync(plan.Id, ct));
    }

    /// <summary>
    /// Two submit intents, same convention as <see cref="Teams(Guid, string, TeamInput, CancellationToken)"/>:
    /// <c>intent=add</c> adds one <see cref="TaskTemplate"/> to the team named by <paramref name="teamId"/>
    /// (a hidden field on that team's own add-task form in <c>Tasks.cshtml</c> — re-checked against this
    /// draft's own teams so a crafted <c>teamId</c> can't attach a task to a team on someone else's plan)
    /// in a single <see cref="IUnitOfWork.SaveChangesAsync"/>, then re-renders step 3 with the new task now
    /// listed; posting "add" again layers on another task, against the same or a different team, across as
    /// many posts as needed. <c>intent=next</c> only advances to <c>/review</c> once at least one of the
    /// draft's teams has at least one task — server-side, so a crafted/replayed request can't skip it —
    /// otherwise it re-renders step 3 with an inline message.
    /// </summary>
    [HttpPost("{id:guid}/tasks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Tasks(Guid id, string intent, Guid teamId, Guid taskId, TaskInput input, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var planId = plan.Id;
        ViewData["step"] = 3;

        if (intent == "add")
        {
            var team = await _uow.Repo<Team>().FirstOrDefaultAsync(t => t.Id == teamId && t.PlanId == planId, ct);
            if (team is null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(input.Title))
            {
                ModelState.AddModelError(nameof(input.Title), _localizer["Validation.TitleRequired"].Value);
                return View(await BuildTasksVmAsync(planId, ct));
            }

            if (input.DurationMinutes <= 0)
            {
                ModelState.AddModelError(nameof(input.DurationMinutes), _localizer["Validation.DurationRequired"].Value);
                return View(await BuildTasksVmAsync(planId, ct));
            }

            var template = new TaskTemplate
            {
                TeamId = teamId,
                Title = input.Title,
                Order = input.Order,
                Duration = TimeSpan.FromMinutes(input.DurationMinutes),
            };
            await _uow.Repo<TaskTemplate>().AddAsync(template, ct);

            await _uow.SaveChangesAsync(ct);
            return View(await BuildTasksVmAsync(planId, ct));
        }

        if (intent == "remove-task")
        {
            // Load the task tracked, then verify (via its team) it belongs to THIS plan before deleting —
            // a crafted taskId can't delete a task on someone else's plan.
            var template = await _uow.Repo<TaskTemplate>().FirstOrDefaultTrackedAsync(t => t.Id == taskId, ct);
            if (template is null)
            {
                return NotFound();
            }

            var owningTeam = await _uow.Repo<Team>().FirstOrDefaultAsync(t => t.Id == template.TeamId && t.PlanId == planId, ct);
            if (owningTeam is null)
            {
                return NotFound();
            }

            _uow.Repo<TaskTemplate>().Remove(template);
            await _uow.SaveChangesAsync(ct);
            return View(await BuildTasksVmAsync(planId, ct));
        }

        // intent == "next": at least one team must have at least one task for this draft.
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var templates = await _uow.Repo<TaskTemplate>().ListAsync(t => teamIds.Contains(t.TeamId), ct);

        if (templates.Count == 0)
        {
            ViewData["tasksBlocked"] = _localizer["Tasks.AtLeastOneRequired"].Value;
            return View(await BuildTasksVmAsync(planId, ct));
        }

        return Redirect($"/admin/plans/create/{planId}/review");
    }

    /// <summary>Assembles <see cref="WizardTasksVm"/> from the draft's current DB state: every team of
    /// the plan, each carrying its own already-persisted <see cref="TaskTemplate"/> rows ordered by
    /// <see cref="TaskTemplate.Order"/>.</summary>
    private async Task<WizardTasksVm> BuildTasksVmAsync(Guid planId, CancellationToken ct)
    {
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var templates = await _uow.Repo<TaskTemplate>().ListAsync(t => teamIds.Contains(t.TeamId), ct);

        return new WizardTasksVm
        {
            PlanId = planId,
            Teams = teams
                .OrderBy(t => t.Name)
                .Select(t => new TeamTasks
                {
                    TeamId = t.Id,
                    TeamName = t.Name,
                    Tasks = templates
                        .Where(tt => tt.TeamId == t.Id)
                        .OrderBy(tt => tt.Order)
                        .Select(tt => new TaskInput
                        {
                            Id = tt.Id,
                            Title = tt.Title,
                            Order = tt.Order,
                            DurationMinutes = (int)tt.Duration.TotalMinutes,
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }

    [HttpGet("{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var stepGuard = await RequireStep(plan.Id, 4, ct);
        if (stepGuard is not null)
        {
            return stepGuard;
        }

        ViewData["step"] = 4;
        return View(await BuildReviewVmAsync(plan.Id, ct));
    }

    /// <summary>
    /// Finish: validates FIRST — the posted <paramref name="roster"/> must be non-empty and every
    /// <see cref="RosterInput.SubstituteForUserId"/> must reference a user who is actually a
    /// <see cref="TeamMembership"/> of one of this draft's teams — before anything touches the change
    /// tracker, so an invalid submission can never leave a partial roster staged even if the steps below
    /// get reordered later. Once valid, stages one <see cref="ShiftAssignment"/> row per roster entry via
    /// <see cref="IRepository{T}.AddAsync"/>, flips the tracked <paramref name="id"/> plan's
    /// <see cref="PlanStatus"/> to <see cref="PlanStatus.Ready"/>, and commits both in the single
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> call below — so a reader can never observe a Ready plan
    /// with no roster, or a rostered plan still stuck on Draft.
    /// </summary>
    [HttpPost("{id:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid id, List<RosterInput> roster, CancellationToken ct)
    {
        var plan = await LoadPlanForEditAsync(id, ct);

        var planId = plan.Id;
        var stepGuard = await RequireStep(planId, 4, ct);
        if (stepGuard is not null)
        {
            return stepGuard;
        }

        ViewData["step"] = 4;
        roster ??= new List<RosterInput>();

        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);
        var validMemberUserIds = memberships.Select(m => m.UserId).ToHashSet();
        // Guard against cross-plan/mass-assignment: every posted row's TeamId AND (TeamId,UserId) pair
        // must belong to THIS draft, so a crafted hidden field can't inject a ShiftAssignment into a
        // foreign plan's team or against a non-member.
        var validTeamIds = teams.Select(t => t.Id).ToHashSet();
        var validPairs = memberships.Select(m => (m.TeamId, m.UserId)).ToHashSet();

        var rosterValid = roster.Count > 0
            && roster.All(r =>
                validTeamIds.Contains(r.TeamId)
                && validPairs.Contains((r.TeamId, r.UserId))
                && (r.SubstituteForUserId is null || validMemberUserIds.Contains(r.SubstituteForUserId.Value)));

        if (!rosterValid)
        {
            var vm = await BuildReviewVmAsync(planId, ct);
            vm.Roster = roster;
            ViewData["reviewBlocked"] = _localizer["Review.RosterInvalid"].Value;
            return View(vm);
        }

        // REPLACE (not append): drop this plan's existing roster rows first, so re-finishing an already-
        // rostered plan (the EDIT flow) overwrites its roster instead of duplicating it. The delete + the
        // re-add + the status flip all commit in the one SaveChangesAsync below, so a reader can never
        // observe the plan with a half-replaced roster.
        foreach (var existing in await _uow.Repo<ShiftAssignment>().ListTrackedAsync(s => teamIds.Contains(s.TeamId), ct))
        {
            _uow.Repo<ShiftAssignment>().Remove(existing);
        }

        foreach (var row in roster)
        {
            await _uow.Repo<ShiftAssignment>().AddAsync(new ShiftAssignment
            {
                TeamId = row.TeamId,
                UserId = row.UserId,
                Shift = row.Shift,
                Date = row.Date,
                SubstituteForUserId = row.SubstituteForUserId,
            }, ct);
        }

        plan.Status = PlanStatus.Ready;

        await _uow.SaveChangesAsync(ct);
        return Redirect($"/admin/plans/{planId}");
    }

    /// <summary>Assembles <see cref="WizardReviewVm"/>: one default <see cref="RosterInput"/> row per
    /// existing <see cref="TeamMembership"/> of the draft (so the roster editor starts pre-populated with
    /// every member who needs a shift) plus <see cref="ReviewReadback"/> — a read-only projection of the
    /// whole plan (info/teams/members/tasks), the same team/membership/task lookups
    /// <c>PlansController.Detail</c> uses for the read-only plan page.</summary>
    private async Task<WizardReviewVm> BuildReviewVmAsync(Guid planId, CancellationToken ct)
    {
        var plan = await _uow.Repo<Plan>().FirstOrDefaultAsync(p => p.Id == planId, ct);
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var memberships = await _uow.Repo<TeamMembership>().ListAsync(m => teamIds.Contains(m.TeamId), ct);
        var templates = await _uow.Repo<TaskTemplate>().ListAsync(t => teamIds.Contains(t.TeamId), ct);
        var userIds = memberships.Select(m => m.UserId).Distinct().ToList();
        var users = await _uow.Repo<User>().ListAsync(u => userIds.Contains(u.Id), ct);

        var teamBlocks = teams
            .OrderBy(t => t.Name)
            .Select(t => new ReviewReadback.TeamBlock(
                t.Id,
                t.Name,
                memberships
                    .Where(m => m.TeamId == t.Id)
                    .Select(m => new ReviewReadback.MemberRow(
                        m.UserId,
                        users.FirstOrDefault(u => u.Id == m.UserId)?.FullName ?? m.UserId.ToString()))
                    .ToList(),
                templates
                    .Where(tt => tt.TeamId == t.Id)
                    .OrderBy(tt => tt.Order)
                    .Select(tt => tt.Title)
                    .ToList()))
            .ToList();

        // Resolve the shift the activation cycle will match on-duty rows against
        // (KuwaitShiftCalculator.Resolve(IClock.UtcNow) — Band + RosterDate, incl. the Kuwait
        // night-after-midnight → previous-day rule). One roster row per membership: seeded from the plan's
        // OWN existing primary ShiftAssignment when it already has one (the EDIT flow shows the real current
        // roster), otherwise defaulted to the current Kuwait shift (the create flow, or newly-added members).
        // Defaulting to the raw wall clock instead would make a manager who accepts the defaults during
        // Evening/Night produce a roster that fails to activate.
        var resolved = _shiftCalc.Resolve(_clock.UtcNow);
        var existingRoster = await _uow.Repo<ShiftAssignment>()
            .ListAsync(s => teamIds.Contains(s.TeamId) && s.SubstituteForUserId == null, ct);
        var roster = memberships
            .Select(m =>
            {
                var current = existingRoster.FirstOrDefault(s => s.TeamId == m.TeamId && s.UserId == m.UserId);
                return new RosterInput
                {
                    TeamId = m.TeamId,
                    UserId = m.UserId,
                    Shift = current?.Shift ?? resolved.Band,
                    Date = current?.Date ?? resolved.RosterDate,
                };
            })
            .ToList();

        return new WizardReviewVm
        {
            PlanId = planId,
            Roster = roster,
            CurrentShift = resolved.Band,
            CurrentDate = resolved.RosterDate,
            Readback = new ReviewReadback
            {
                PlanName = plan!.Name,
                Type = plan.Type,
                Objective = plan.Objective,
                Description = plan.Description,
                Scope = plan.Scope,
                Teams = teamBlocks,
            },
        };
    }
}
