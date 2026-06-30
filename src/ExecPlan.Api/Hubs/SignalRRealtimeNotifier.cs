using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Dashboard;
using Microsoft.AspNetCore.SignalR;

namespace ExecPlan.Api.Hubs;

/// <summary>
/// The SignalR-backed <see cref="IRealtimeNotifier"/> (replaces <c>NoOpRealtimeNotifier</c> in the API
/// host — see <c>Program.cs</c>). Application services call <see cref="DashboardChangedAsync"/> /
/// <see cref="ActivationClosedAsync"/> AFTER their single atomic commit; this recomputes the snapshot
/// via <see cref="IDashboardService"/> and pushes it to the activation's <c>act-{id}</c> group. The
/// Application layer stays SignalR-free (it depends only on <see cref="IRealtimeNotifier"/>); the
/// concrete hub coupling lives here in the Api layer.
/// </summary>
public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<DashboardHub> _hub;
    private readonly IDashboardService _dashboard;

    public SignalRRealtimeNotifier(IHubContext<DashboardHub> hub, IDashboardService dashboard)
    {
        _hub = hub;
        _dashboard = dashboard;
    }

    public async Task DashboardChangedAsync(Guid activationId, CancellationToken ct = default)
    {
        var dto = await _dashboard.GetSnapshotAsync(activationId, ct);
        await _hub.Clients.Group(DashboardHub.GroupName(activationId))
            .SendAsync("DashboardUpdated", dto, ct);
    }

    public Task ActivationClosedAsync(Guid activationId, CancellationToken ct = default) =>
        _hub.Clients.Group(DashboardHub.GroupName(activationId))
            .SendAsync("ActivationClosed", activationId, ct);
}
