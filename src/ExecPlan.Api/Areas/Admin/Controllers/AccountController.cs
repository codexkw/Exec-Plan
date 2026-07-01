using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ExecPlan.Api.Areas.Admin.Models;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>Sign-in/out for the MVC admin area. This task wires the Login GET (form render) and the
/// Denied view only; Login POST + Logout arrive in Task 3 alongside <c>AdminClaimsPrincipalFactory</c>.</summary>
[Area("Admin")]
[AllowAnonymous]
[Route("admin")]
public sealed class AccountController : Controller
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User?.Identity?.IsAuthenticated == true) return Redirect("/admin");
        return View(new LoginVm { ReturnUrl = returnUrl });
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();
}
