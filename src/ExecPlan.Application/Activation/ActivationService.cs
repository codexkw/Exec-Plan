using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Application.Shifts;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Activation;

/// <summary>
/// Implements the activation cycle (design §5.3). Guards (exists / authorized / not-already-active)
/// → resolve the Kuwait shift → snapshot the on-duty roster into <see cref="ActivationParticipant"/>
/// rows (freezing team name and the resolved substitute) → generate <see cref="ExecutionTask"/>s
/// from each team's templates with <c>DueAtUtc = ActivatedAtUtc + template.Duration</c> → stage the
/// first notification + call attempt per participant. Everything is staged into the unit of work and
/// committed with exactly ONE <see cref="IUnitOfWork.SaveChangesAsync"/> (EF's implicit transaction =
/// atomic, NFR-8); the realtime dashboard push happens only after the commit succeeds.
/// </summary>
public sealed class ActivationService : IActivationService
{
    private readonly IUnitOfWork _uow;
    private readonly IClock _clock;
    private readonly KuwaitShiftCalculator _shiftCalc;
    private readonly INotificationProvider _provider;
    private readonly IRealtimeNotifier _realtime;
    private readonly EscalationOptions _escalationOptions;

    public ActivationService(
        IUnitOfWork uow,
        IClock clock,
        KuwaitShiftCalculator shiftCalc,
        INotificationProvider provider,
        IRealtimeNotifier realtime,
        EscalationOptions escalationOptions)
    {
        _uow = uow;
        _clock = clock;
        _shiftCalc = shiftCalc;
        _provider = provider;
        _realtime = realtime;
        _escalationOptions = escalationOptions;
    }

    public async Task<Guid> ActivateAsync(Guid planId, Guid actingUserId, CancellationToken ct = default)
    {
        // 1. Plan must exist.
        var plan = await _uow.Repo<Plan>().GetByIdAsync(planId, ct);
        if (plan is null)
        {
            throw AppException.NotFound("Plan not found.");
        }

        // 2. Authorization: SystemAdmin, the plan creator, or a registered PlanActivator.
        var user = await _uow.Repo<User>().GetByIdAsync(actingUserId, ct);
        var isAdmin = user is not null && user.Role == UserRole.SystemAdmin;
        var isCreator = plan.CreatedByUserId == actingUserId;
        var isActivator = await _uow.Repo<PlanActivator>()
            .FirstOrDefaultAsync(a => a.PlanId == planId && a.UserId == actingUserId, ct) is not null;
        if (!(isAdmin || isCreator || isActivator))
        {
            throw AppException.Forbidden("You are not allowed to activate this plan.");
        }

        // 3. Already-active guard: only one Active activation per plan at a time.
        var existingActive = await _uow.Repo<PlanActivation>()
            .FirstOrDefaultAsync(a => a.PlanId == planId && a.Status == ActivationStatus.Active, ct);
        if (existingActive is not null)
        {
            throw AppException.Conflict("This plan is already active.", "PlanAlreadyActive");
        }

        // 4. Resolve the Kuwait shift band + roster date for "now".
        var r = _shiftCalc.Resolve(_clock.UtcNow);
        var band = r.Band;
        var rosterDate = r.RosterDate;

        // 5. On-duty resolution from the roster.
        var teams = await _uow.Repo<Team>().ListAsync(t => t.PlanId == planId, ct);
        var teamIds = teams.Select(t => t.Id).ToList();

        var onDuty = await _uow.Repo<ShiftAssignment>().ListAsync(
            sa => teamIds.Contains(sa.TeamId)
                  && sa.Shift == band
                  && sa.Date == rosterDate
                  && sa.SubstituteForUserId == null,
            ct);
        if (onDuty.Count == 0)
        {
            throw AppException.Conflict("No one is on duty for this shift.", "NoOneOnDuty");
        }

        var substituteRows = await _uow.Repo<ShiftAssignment>().ListAsync(
            sa => teamIds.Contains(sa.TeamId)
                  && sa.Shift == band
                  && sa.Date == rosterDate
                  && sa.SubstituteForUserId != null,
            ct);
        var substituteFor = new Dictionary<Guid, Guid>();
        foreach (var sa in substituteRows)
        {
            // Key = the user being stood in for; value = the stand-in (the substitute row's UserId).
            substituteFor[sa.SubstituteForUserId!.Value] = sa.UserId;
        }

        var now = _clock.UtcNow;

        // 6. Create the activation snapshot (Guid PK is ctor-assigned, so its Id is known now).
        var activation = new PlanActivation
        {
            PlanId = planId,
            Status = ActivationStatus.Active,
            Shift = band,
            RosterDate = rosterDate,
            ActivatedByUserId = actingUserId,
            ActivatedAtUtc = now,
            EscalationThreshold = _escalationOptions.DefaultThreshold,
        };
        await _uow.Repo<PlanActivation>().AddAsync(activation, ct);

        // 7. One participant per on-duty assignment, their tasks, and the first call.
        foreach (var sa in onDuty)
        {
            var team = teams.First(t => t.Id == sa.TeamId);

            var participant = new ActivationParticipant
            {
                ActivationId = activation.Id,
                UserId = sa.UserId,
                TeamId = sa.TeamId,
                TeamNameSnapshot = team.Name,
                Status = ParticipantStatus.Pending,
                ResolvedSubstituteUserId =
                    substituteFor.TryGetValue(sa.UserId, out var sub) ? sub : null,
                CallAttemptCount = 0,
            };
            await _uow.Repo<ActivationParticipant>().AddAsync(participant, ct);

            var templates = await _uow.Repo<TaskTemplate>().ListAsync(tt => tt.TeamId == sa.TeamId, ct);
            foreach (var tpl in templates)
            {
                var task = new ExecutionTask
                {
                    ActivationId = activation.Id,
                    ParticipantId = participant.Id,
                    Title = tpl.Title,
                    Order = tpl.Order,
                    Status = ExecTaskStatus.Pending,
                    DueAtUtc = activation.ActivatedAtUtc + tpl.Duration,
                    SourceTaskTemplateId = tpl.Id,
                };
                await _uow.Repo<ExecutionTask>().AddAsync(task, ct);
            }

            _provider.StageNotification(
                activation.Id, participant.UserId, NotificationKind.Notification,
                ActivationNoticeBody, now);
            _provider.StageCallAttempt(activation.Id, participant.Id, 1, now);
            participant.CallAttemptCount = 1;
        }

        // 8. Commit everything atomically, then push realtime.
        await _uow.SaveChangesAsync(ct);
        await _realtime.DashboardChangedAsync(activation.Id, ct);

        return activation.Id;
    }

    private const string ActivationNoticeBody = "تم تفعيل الخطة. الرجاء تأكيد الجاهزية.";
}
