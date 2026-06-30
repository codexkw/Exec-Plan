using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Cli;

/// <summary>
/// Trivial <see cref="ICurrentUser"/> for the CLI host. There is no authenticated principal in a CLI
/// invocation, but <c>AddApplication</c> also registers <c>ExecutionService</c>/<c>BroadcastService</c>,
/// which require <see cref="ICurrentUser"/> in their constructors. The CLI's <c>run-escalation</c> path
/// only ever resolves <see cref="ExecPlan.Application.Escalation.IEscalationService"/>, which has no
/// such dependency — so this no-op (always-anonymous) implementation exists purely so the DI container
/// is fully resolvable end to end; it is never consulted for an authorization decision in this process.
/// </summary>
public sealed class NoOpCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
    public UserRole? Role => null;
    public bool IsInRole(UserRole r) => false;
}
