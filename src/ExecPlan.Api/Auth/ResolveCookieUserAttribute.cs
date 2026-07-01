using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ExecPlan.Api.Auth;

/// <summary>
/// Authenticates the <see cref="AuthPolicies.AdminCookieScheme"/> for an endpoint and, on success, assigns
/// the resulting principal to <c>HttpContext.User</c> — but never gates the request: an anonymous caller
/// (no cookie) simply proceeds unauthenticated.
/// </summary>
/// <remarks>
/// Why this exists: the host's DEFAULT authentication scheme is JWT (for the <c>/api</c> surface), so
/// <c>UseAuthentication()</c> leaves <c>HttpContext.User</c> anonymous on browser POSTs that are not
/// attributed with the cookie scheme. Antiforgery tokens minted on cookie-authenticated admin pages are
/// bound to the cookie user, so validating one requires <c>HttpContext.User</c> to BE that cookie user.
/// On an <c>[AllowAnonymous]</c> endpoint that still needs the ambient cookie identity (the culture toggle:
/// it must work both before login — anonymous — and after — cookie user), the natural
/// <c>[Authorize(Scheme = AdminCookie)] + [AllowAnonymous]</c> pairing trips analyzer ASP0026. This filter
/// gives the same "authenticate but don't require" behavior without that pairing. It implements
/// <see cref="IOrderedFilter"/> with <see cref="int.MinValue"/> so it runs before the framework's
/// antiforgery authorization filter, which reads <c>HttpContext.User</c> when it validates.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ResolveCookieUserAttribute : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return;
        }

        var result = await context.HttpContext.AuthenticateAsync(AuthPolicies.AdminCookieScheme);
        if (result.Succeeded && result.Principal is not null)
        {
            context.HttpContext.User = result.Principal;
        }
    }
}
