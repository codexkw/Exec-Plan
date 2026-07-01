using ExecPlan.Api.Auth;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>Role landing page for <c>/admin</c>: SystemAdmin/PlanManager go straight to the plans list,
/// TeamLeader to their activation dashboard list (Task 14 builds the real view — until then this route
/// 404s, which is expected), and anyone else back to login.</summary>
[Area("Admin")]
[Route("admin")]
[Authorize(AuthenticationSchemes = AuthPolicies.AdminCookieScheme)]
public sealed class HomeController : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        if (User.IsInRole(nameof(UserRole.SystemAdmin)) || User.IsInRole(nameof(UserRole.PlanManager)))
            return Redirect("/admin/plans");
        if (User.IsInRole(nameof(UserRole.TeamLeader)))
            return Redirect("/admin/activations"); // thin leader landing (Task 14 adds the list view)
        return Redirect("/admin/login");
    }
}
