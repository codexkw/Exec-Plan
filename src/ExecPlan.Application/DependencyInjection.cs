using ExecPlan.Application.Activation;
using ExecPlan.Application.Broadcast;
using ExecPlan.Application.Common;
using ExecPlan.Application.Dashboard;
using ExecPlan.Application.Escalation;
using ExecPlan.Application.Execution;
using ExecPlan.Application.Shifts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.Application;

/// <summary>
/// Registers the Application service layer. Pure abstractions only — no EF Core / SignalR — so the
/// clean-architecture dependency direction holds: hosts call <c>AddInfrastructure</c> (which supplies
/// <c>IUnitOfWork</c>/<c>IClock</c>/<c>INotificationProvider</c>/<c>IRealtimeNotifier</c> and the auth
/// services) and then <c>AddApplication</c>. <c>ICurrentUser</c> is registered by the host (the Api
/// reads it from the request principal).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection s, IConfiguration cfg)
    {
        // Activation-time escalation threshold, stamped onto each PlanActivation. Singleton — immutable
        // config snapshot shared by ActivationService (writes) and EscalationService (reads).
        s.AddSingleton(new EscalationOptions
        {
            DefaultThreshold = cfg.GetValue<int?>("Escalation:DefaultThreshold") ?? 5,
        });

        // Pure, dependency-free shift resolver — singleton is safe (no per-request state).
        s.AddSingleton<KuwaitShiftCalculator>();

        s.AddScoped<IActivationService, ActivationService>();
        s.AddScoped<IEscalationService, EscalationService>();
        s.AddScoped<IDashboardService, DashboardService>();
        s.AddScoped<AcknowledgeService>();
        s.AddScoped<ExecutionService>();
        s.AddScoped<BroadcastService>();

        return s;
    }
}
