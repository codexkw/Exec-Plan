namespace ExecPlan.Application.Dashboard;

/// <summary>
/// Builds the single aggregated monitoring snapshot for an activation (design §5.5). Pure read: it
/// loads each entity set for the activation and aggregates in memory (DEC-14 — no multi-collection
/// EF Includes, which also sidesteps cartesian explosion), performs no mutation, and fires no realtime
/// push. The same snapshot is served by REST and pushed over the SignalR hub.
/// </summary>
public interface IDashboardService
{
    Task<DashboardDto> GetSnapshotAsync(Guid activationId, CancellationToken ct = default);
}
