using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Cli;
using ExecPlan.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

// The CLI never authenticates a request, but AddApplication also registers ExecutionService/
// BroadcastService, which take ICurrentUser in their constructor. Register a trivial no-op so the
// whole DI graph is resolvable (see ValidateOnBuild below) — run-escalation never resolves those two
// services, so the no-op is never actually consulted for an authorization decision.
builder.Services.AddScoped<ICurrentUser, NoOpCurrentUser>();

// Fail fast on a misconfigured registration at startup rather than mid-cycle: validate the whole DI
// graph (and scope captures) when the container is built, regardless of hosting environment.
builder.ConfigureContainer(new DefaultServiceProviderFactory(
    new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));

using var host = builder.Build();

const string usage = "Usage: dotnet run --project src/ExecPlan.Cli -- run-escalation --activation <guid> | --all-active";

if (args.Length == 0 || args[0] != "run-escalation")
{
    Console.Error.WriteLine(usage);
    return EscalationRunner.ExitBadArgs;
}

var escalationArgs = EscalationArgs.Parse(args[1..]);
if (escalationArgs is null)
{
    Console.Error.WriteLine(usage);
    return EscalationRunner.ExitBadArgs;
}

var runner = new EscalationRunner(host.Services);
return await runner.RunAsync(escalationArgs, Console.Out);
