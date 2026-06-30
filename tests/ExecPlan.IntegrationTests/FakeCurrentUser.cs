using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// Settable <see cref="ICurrentUser"/> test double for services that read the acting user from the
/// current-user seam (ExecutionService/BroadcastService). Mirrors how the Api host supplies the actor
/// from the request principal, without an HTTP context.
/// </summary>
public sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public UserRole? Role { get; set; }
    public bool IsInRole(UserRole r) => Role == r;
}
