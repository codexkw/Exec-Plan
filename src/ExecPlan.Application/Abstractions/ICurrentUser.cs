using ExecPlan.Domain.Enums;

namespace ExecPlan.Application.Abstractions;

public interface ICurrentUser
{
    Guid? UserId { get; }
    UserRole? Role { get; }
    bool IsInRole(UserRole r);
}
