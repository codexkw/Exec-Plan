using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace ExecPlan.Api.Auth;

/// <summary>
/// <see cref="ICurrentUser"/> backed by <see cref="IHttpContextAccessor"/>. Reads the SAME claim
/// types <c>JwtTokenFactory</c> emits (<c>sub</c> for the user id, <c>ClaimTypes.Role</c> for the role
/// — see Program.cs's <c>MapInboundClaims = false</c> + <c>RoleClaimType</c> wiring). Returns null for
/// every member when there is no authenticated user on the current request.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var raw = Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public UserRole? Role
    {
        get
        {
            var raw = Principal?.FindFirst(ClaimTypes.Role)?.Value;
            return Enum.TryParse<UserRole>(raw, out var role) ? role : null;
        }
    }

    public bool IsInRole(UserRole r) => Role == r;
}
