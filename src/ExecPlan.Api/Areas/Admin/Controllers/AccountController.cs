using ExecPlan.Api.Auth;
using ExecPlan.Application.Auth;
using ExecPlan.Application.Common;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ExecPlan.Api.Areas.Admin.Models;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Sign-in/out for the MVC admin area. <c>[AllowAnonymous]</c> lives on the individual anonymous
/// actions (<see cref="Login(string?)"/>, <see cref="Login(LoginVm, CancellationToken)"/>,
/// <see cref="Denied"/>) rather than the class, because a controller-level <c>[AllowAnonymous]</c>
/// would make <see cref="Logout"/>'s action-level <c>[Authorize]</c> be ignored (ASP.NET Core:
/// AllowAnonymous anywhere on an endpoint wins over Authorize on that same endpoint) — Logout must
/// stay genuinely gated behind the admin cookie.
/// </summary>
[Area("Admin")]
[Route("admin")]
public sealed class AccountController : Controller
{
    private readonly IAuthService _auth;
    public AccountController(IAuthService auth) => _auth = auth;

    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true) return Redirect("/admin");
        return View(new LoginVm { ReturnUrl = returnUrl });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginVm vm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vm.UserName) || string.IsNullOrWhiteSpace(vm.Password))
        {
            vm.Error = "Login.Invalid";
            return View(vm);
        }

        AppUserPrincipal principal;
        try
        {
            principal = await _auth.ValidateCredentialsAsync(vm.UserName!, vm.Password!, ct);
        }
        catch (AppException ex) when (ex.ErrorKind == AppException.Kind.Unauthorized)
        {
            vm.Error = "Login.Invalid";
            return View(vm);
        }

        if (principal.Role == UserRole.TeamMember) // no web surface
        {
            vm.Error = "Login.MemberBlocked";
            return View(vm);
        }

        await HttpContext.SignInAsync(AuthPolicies.AdminCookieScheme,
            AdminClaimsPrincipalFactory.Create(principal),
            new AuthenticationProperties { IsPersistent = false });

        return LocalRedirect(SafeReturnUrl(vm.ReturnUrl) ?? "/admin");
    }

    [HttpPost("logout")]
    [Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AuthPolicies.AdminCookieScheme);
        return Redirect("/admin/login");
    }

    [HttpGet("denied")]
    [AllowAnonymous]
    public IActionResult Denied() => View();

    private string? SafeReturnUrl(string? url)
        => (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url)) ? url : null; // open-redirect guard
}
