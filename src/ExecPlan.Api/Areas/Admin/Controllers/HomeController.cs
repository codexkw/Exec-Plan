using ExecPlan.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>Minimal authenticated landing page for <c>/admin</c>; fleshed out in Task 3 with the real
/// dashboard/plans redirect logic once sign-in exists.</summary>
[Area("Admin")]
[Route("admin")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
public sealed class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index() => Redirect("/admin/plans");
}
