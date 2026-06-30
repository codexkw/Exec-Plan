namespace ExecPlan.Application.Abstractions;

public interface IRealtimeNotifier
{
    Task DashboardChangedAsync(Guid activationId, CancellationToken ct = default);
    Task ActivationClosedAsync(Guid activationId, CancellationToken ct = default);
}
