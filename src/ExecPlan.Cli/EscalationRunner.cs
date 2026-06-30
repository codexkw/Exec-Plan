using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Common;
using ExecPlan.Application.Escalation;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.Cli;

/// <summary>
/// Drives <see cref="IEscalationService.RunCycleAsync"/> from the CLI process. Resolves
/// <see cref="IEscalationService"/> from a fresh DI scope per activation (mirroring per-request
/// scoping elsewhere in this codebase) so the CLI and the dashboard's "run escalation now" action are
/// provably the SAME code path (FR-ESC-1) — only the trigger differs. Carries no process-specific
/// behavior (no <see cref="Console"/> calls) so it is unit-testable in-process against any
/// <see cref="IServiceProvider"/>, including one wired directly to a test SQLite fixture.
/// </summary>
public sealed class EscalationRunner
{
    /// <summary>Bad/missing arguments — nothing was attempted.</summary>
    public const int ExitBadArgs = 1;

    /// <summary>Arguments were valid but running the cycle(s) failed (e.g. activation not found).</summary>
    public const int ExitError = 2;

    public const int ExitSuccess = 0;

    private readonly IServiceProvider _services;

    public EscalationRunner(IServiceProvider services) => _services = services;

    public async Task<int> RunAsync(EscalationArgs args, TextWriter output, CancellationToken ct = default)
    {
        if (args.ActivationId is { } activationId)
        {
            return await RunOneAsync(activationId, output, ct);
        }

        return await RunAllActiveAsync(output, ct);
    }

    private async Task<int> RunOneAsync(Guid activationId, TextWriter output, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var escalation = scope.ServiceProvider.GetRequiredService<IEscalationService>();

        try
        {
            var result = await escalation.RunCycleAsync(activationId, ct);
            output.WriteLine(Format(activationId, result));
            return ExitSuccess;
        }
        catch (AppException ex)
        {
            output.WriteLine($"activation {activationId}: error ({ex.ErrorKind}) {ex.Message}");
            return ExitError;
        }
    }

    private async Task<int> RunAllActiveAsync(TextWriter output, CancellationToken ct)
    {
        List<Guid> activeIds;
        using (var scope = _services.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var active = await uow.Repo<PlanActivation>()
                .ListAsync(a => a.Status == ActivationStatus.Active, ct);
            activeIds = active.Select(a => a.Id).ToList();
        }

        if (activeIds.Count == 0)
        {
            output.WriteLine("no active activations.");
            return ExitSuccess;
        }

        var exitCode = ExitSuccess;
        foreach (var id in activeIds)
        {
            var rc = await RunOneAsync(id, output, ct);
            if (rc != ExitSuccess)
            {
                exitCode = rc;
            }
        }

        return exitCode;
    }

    private static string Format(Guid activationId, EscalationCycleResult result) =>
        $"activation {activationId}: attemptsAdded={result.AttemptsAdded} inducted={result.Inducted}";
}
