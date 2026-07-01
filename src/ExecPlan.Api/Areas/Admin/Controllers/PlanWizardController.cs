using ExecPlan.Api.Areas.Admin.Models;
using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
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

    public PlanWizardController(IUnitOfWork uow, ICurrentUser me, IStringLocalizer<Resources.SharedResource> localizer)
    {
        _uow = uow;
        _me = me;
        _localizer = localizer;
    }

    /// <summary>
    /// Reusable ownership guard for every wizard step (this task's <see cref="Teams"/> and the
    /// Tasks/Review actions Tasks 11-12 add later): loads the plan by id TRACKED (so the caller can
    /// mutate it and rely on a later <c>SaveChangesAsync</c> to persist that), and enforces that only
    /// the Draft's own creator (or a SystemAdmin) may keep editing it, and only while it is still a
    /// Draft. A finalized (non-Draft) plan isn't a "you're not allowed" case — it's "there's nothing left
    /// to wizard here" — so that path returns a plain redirect to the read-only plan detail page rather
    /// than an <see cref="AppException"/>. Missing plan / wrong owner ARE <see cref="AppException"/>s
    /// (NotFound / Forbidden respectively), consistently mapped by <c>AppExceptionMiddleware</c> to the
    /// shared NotFound/denied pages. Callers must check <c>ShortCircuit</c> first and return it as-is.
    /// </summary>
    private async Task<(Plan? Plan, IActionResult? ShortCircuit)> LoadDraftForEditAsync(Guid id, CancellationToken ct)
    {
        var plan = await _uow.Repo<Plan>().FirstOrDefaultTrackedAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            throw AppException.NotFound($"Plan {id} was not found.");
        }

        if (plan.Status != PlanStatus.Draft)
        {
            return (null, Redirect($"/admin/plans/{id}"));
        }

        var isAdmin = User.IsInRole(nameof(UserRole.SystemAdmin));
        if (plan.CreatedByUserId != _me.UserId && !isAdmin)
        {
            throw AppException.Forbidden($"You do not own plan {id}.");
        }

        return (plan, null);
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
            ModelState.AddModelError(nameof(vm.Name), "required");
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
    /// Wizard step-navigation guard (Task 12): given the requested step number, checks that every
    /// EARLIER step's completion prerequisite already holds for this draft, and returns a redirect to the
    /// earliest unmet one's URL if not — so a manager can't deep-link straight to <c>/tasks</c> or
    /// <c>/review</c> on a draft that never got a team+member or a task. Returns null once every
    /// prerequisite up to <paramref name="step"/> holds, meaning the caller should render normally. Step
    /// 2's only prerequisite is "this is still a Draft", already enforced by
    /// <see cref="LoadDraftForEditAsync"/>, so requesting step 2 is a no-op here; the checks only start
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
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var stepGuard = await RequireStep(plan!.Id, 2, ct);
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
    public async Task<IActionResult> Teams(Guid id, string intent, TeamInput input, CancellationToken ct)
    {
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var planId = plan!.Id;
        ViewData["step"] = 2;

        if (intent == "add")
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                ModelState.AddModelError(nameof(input.Name), "required");
                return View(await BuildTeamsVmAsync(planId, ct));
            }

            var team = new Team
            {
                PlanId = planId,
                Name = input.Name,
                TeamLeaderUserId = input.TeamLeaderUserId,
            };
            await _uow.Repo<Team>().AddAsync(team, ct);

            foreach (var userId in (input.MemberUserIds ?? new List<Guid>()).Distinct())
            {
                await _uow.Repo<TeamMembership>().AddAsync(new TeamMembership { TeamId = team.Id, UserId = userId }, ct);
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
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var stepGuard = await RequireStep(plan!.Id, 3, ct);
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
    public async Task<IActionResult> Tasks(Guid id, string intent, Guid teamId, TaskInput input, CancellationToken ct)
    {
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var planId = plan!.Id;
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
                ModelState.AddModelError(nameof(input.Title), "required");
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
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var stepGuard = await RequireStep(plan!.Id, 4, ct);
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
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        var planId = plan!.Id;
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

        var rosterValid = roster.Count > 0
            && roster.All(r => r.SubstituteForUserId is null || validMemberUserIds.Contains(r.SubstituteForUserId.Value));

        if (!rosterValid)
        {
            var vm = await BuildReviewVmAsync(planId, ct);
            vm.Roster = roster;
            ViewData["reviewBlocked"] = _localizer["Review.RosterInvalid"].Value;
            return View(vm);
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

        var today = DateTime.UtcNow.Date;
        var roster = memberships
            .Select(m => new RosterInput
            {
                TeamId = m.TeamId,
                UserId = m.UserId,
                Shift = ShiftBand.Morning,
                Date = today,
            })
            .ToList();

        return new WizardReviewVm
        {
            PlanId = planId,
            Roster = roster,
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
