using ExecPlan.Application.Abstractions;

namespace ExecPlan.Infrastructure.Realtime;

/// <summary>
/// No-op <see cref="IRealtimeNotifier"/> so services that depend on it can resolve from the
/// container before the real SignalR-backed <c>DashboardHub</c> notifier exists. Replace the DI
/// registration with the SignalR implementation in a later task; no other code changes.
/// </summary>
public sealed class NoOpRealtimeNotifier : IRealtimeNotifier
{
    public Task DashboardChangedAsync(Guid activationId, CancellationToken ct = default) => Task.CompletedTask;

    public Task ActivationClosedAsync(Guid activationId, CancellationToken ct = default) => Task.CompletedTask;
}
