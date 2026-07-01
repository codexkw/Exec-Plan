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
/// <see cref="Info"/> (step 1: plan info -> Draft); this task (10) adds <see cref="Teams"/> (step 2:
/// teams &amp; members) onto the same <c>{id}</c>; Tasks 11-12 add tasks/review. Class gate is
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

    [HttpGet("{id:guid}/teams")]
    public async Task<IActionResult> Teams(Guid id, CancellationToken ct)
    {
        var (plan, shortCircuit) = await LoadDraftForEditAsync(id, ct);
        if (shortCircuit is not null)
        {
            return shortCircuit;
        }

        ViewData["step"] = 2;
        return View(await BuildTeamsVmAsync(plan!.Id, ct));
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
}
