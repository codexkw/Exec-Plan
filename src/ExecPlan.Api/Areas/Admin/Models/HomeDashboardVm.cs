using ExecPlan.Domain.Enums;

namespace ExecPlan.Api.Areas.Admin.Models;

/// <summary>
/// View model for the admin landing dashboard (<c>GET /admin</c>, Manager/Admin). A tenant-wide operational
/// overview: plan inventory, live activation load, the readiness pulse aggregated across every currently
/// Active activation's participants, and the most recent activations. Manager authority is tenant-global
/// (DECISIONS Open note), and Manager/Admin are unrestricted activation viewers
/// (<c>ActivationsController.EnsureMayViewAsync</c>), so the global figures and the "watch" links are safe.
/// </summary>
public sealed class HomeDashboardVm
{
    public string? DisplayName { get; init; }

    public int PlansTotal { get; init; }
    public int PlansReady { get; init; }
    public int PlansDraft { get; init; }
    public int ActiveActivations { get; init; }
    public int UsersTotal { get; init; }
    public int DepartmentsTotal { get; init; }
    public int OrganizationsTotal { get; init; }
    public int TeamsTotal { get; init; }

    // Readiness pulse across all Active activations' participants.
    public int Pending { get; init; }
    public int Ready { get; init; }
    public int Escalated { get; init; }
    public int Inducted { get; init; }

    public int ParticipantsTotal => Pending + Ready + Escalated + Inducted;

    /// <summary>Ready = responded (<see cref="ParticipantStatus.Ready"/>) or an inducted substitute now covering.</summary>
    public int ReadinessPercent =>
        ParticipantsTotal == 0 ? 0 : (int)Math.Round((Ready + Inducted) * 100.0 / ParticipantsTotal);

    public IReadOnlyList<Row> Recent { get; init; } = [];

    public sealed record Row(Guid ActivationId, string PlanName, ShiftBand Shift, ActivationStatus Status, DateTime ActivatedAtUtc);
}
