using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Themed HTML error pages that <see cref="ExecPlan.Api.Middleware.AppExceptionMiddleware"/> redirects
/// HTML (non-JSON) requests to when an <see cref="ExecPlan.Application.Common.AppException"/> — or, for
/// <see cref="Error"/>, any unhandled exception — escapes a downstream handler. Fully anonymous: an
/// unauthenticated request can trip NotFound/Validation just as easily as an authenticated one, and
/// these pages must never themselves require the very cookie that might be exactly what's missing
/// (Forbidden already has its own anonymous destination, <c>Account.Denied</c>, from Task 3).
/// </summary>
[Area("Admin")]
[Route("admin")]
[AllowAnonymous]
public sealed class ErrorPageController : Controller
{
    [HttpGet("notfound")]
    public IActionResult NotFound(string? msg)
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        ViewBag.Msg = msg;
        return View("NotFound");
    }

    [HttpGet("error")]
    public IActionResult Error(string? msg)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        ViewBag.Msg = msg;
        return View("Error");
    }
}
