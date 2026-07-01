using ExecPlan.Api.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

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
    private readonly IStringLocalizer<SharedResource> _localizer;

    public ErrorPageController(IStringLocalizer<SharedResource> localizer) => _localizer = localizer;

    [HttpGet("notfound")]
    public IActionResult NotFound(string? msg)
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        ViewBag.Msg = msg;
        return View("NotFound");
    }

    /// <summary>
    /// Generic Validation/Conflict error page. The middleware passes a stable <paramref name="code"/>
    /// (never the raw English exception message); we resolve it to the localized <c>AppError.&lt;code&gt;</c>
    /// message here, falling back to <c>AppError.Generic</c> when there is no code (or an unknown one) — so
    /// the Arabic admin never renders an English literal.
    /// </summary>
    [HttpGet("error")]
    public IActionResult Error(string? code)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;

        var localized = string.IsNullOrEmpty(code) ? null : _localizer["AppError." + code];
        ViewBag.Msg = localized is null || localized.ResourceNotFound
            ? _localizer["AppError.Generic"].Value
            : localized.Value;

        return View("Error");
    }
}
