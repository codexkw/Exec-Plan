using ExecPlan.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Areas.Admin.Controllers;

/// <summary>
/// Test-only throw route (Task 4) that lets <c>ErrorMappingTests</c> exercise
/// <see cref="ExecPlan.Api.Middleware.AppExceptionMiddleware"/>'s HTML content-negotiation branch end to
/// end over real HTTP, without depending on a real business action that happens to throw each
/// <see cref="AppException.Kind"/>. Gated to <see cref="IWebHostEnvironment.IsDevelopment"/> (confirmed
/// empirically: <c>TestAppFactory</c>'s hosted <c>Program</c> runs with <c>EnvironmentName=Development</c>,
/// same as <c>ExecPlan.Api/Properties/launchSettings.json</c>'s local-dev profiles) so this route 404s
/// and effectively does not exist under a Production deployment.
/// </summary>
[Area("Admin")]
[Route("admin/_throw")]
[AllowAnonymous]
public sealed class ThrowController : Controller
{
    private readonly IWebHostEnvironment _env;
    public ThrowController(IWebHostEnvironment env) => _env = env;

    [HttpGet("{kind}")]
    public IActionResult Throw(string kind)
    {
        if (!_env.IsDevelopment())
        {
            return NotFound();
        }

        throw kind.ToLowerInvariant() switch
        {
            "notfound" => AppException.NotFound("Test not-found (ThrowController)."),
            "forbidden" => AppException.Forbidden("Test forbidden (ThrowController)."),
            "unauthorized" => AppException.Unauthorized("Test unauthorized (ThrowController)."),
            "conflict" => AppException.Conflict("Test conflict (ThrowController)."),
            "validation" => AppException.Validation("Test validation (ThrowController)."),
            _ => new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown throw kind — used to test the middleware's non-AppException HTML fallback."),
        };
    }
}
