using ExecPlan.Application.Activation;
using ExecPlan.Application.Broadcast;
using ExecPlan.Application.Dashboard;
using ExecPlan.Application.Escalation;
using ExecPlan.Application.Execution;
using ExecPlan.Application.Shifts;
using ExecPlan.Application.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Proves AddApplication's registrations resolve from the REAL host container (Program.cs →
/// AddInfrastructure + AddApplication, with the host-supplied ICurrentUser). Resolving a service
/// forces its whole constructor dependency graph to be satisfied, so this is the durable guard that
/// "every Application service exists and is DI-wired" (T16).
/// </summary>
public class DependencyInjectionTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public DependencyInjectionTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public void All_application_services_resolve_from_the_host_container()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<KuwaitShiftCalculator>().Should().NotBeNull();
        sp.GetRequiredService<EscalationOptions>().Should().NotBeNull();
        sp.GetRequiredService<IActivationService>().Should().BeOfType<ActivationService>();
        sp.GetRequiredService<IEscalationService>().Should().BeOfType<EscalationService>();
        sp.GetRequiredService<IDashboardService>().Should().BeOfType<DashboardService>();
        sp.GetRequiredService<AcknowledgeService>().Should().NotBeNull();
        sp.GetRequiredService<ExecutionService>().Should().NotBeNull();
        sp.GetRequiredService<BroadcastService>().Should().NotBeNull();
    }

    [Fact]
    public void Escalation_threshold_defaults_to_five()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<EscalationOptions>().DefaultThreshold.Should().Be(5);
    }
}
