namespace ExecPlan.Api.Auth;

/// <summary>
/// Authentication scheme name + authorization policy names used by the host. Policies map to
/// <see cref="ExecPlan.Domain.Enums.UserRole"/> values exactly as <c>JwtTokenFactory</c> emits them
/// (the enum name, e.g. <c>"SystemAdmin"</c>) via <see cref="Microsoft.AspNetCore.Authorization.AuthorizationOptions"/>.
/// </summary>
public static class AuthPolicies
{
    /// <summary>Cookie scheme reserved for the future MVC admin area sign-in.</summary>
    public const string AdminCookieScheme = "AdminCookie";

    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Leader = "Leader";
    public const string Member = "Member";
    public const string ManagerOrAdmin = "ManagerOrAdmin";
}
