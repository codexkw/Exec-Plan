using ExecPlan.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>Protected diagnostic endpoint proving the auth pipeline (JWT bearer + claim mapping +
/// <see cref="ICurrentUser"/>) is wired correctly end to end. Real CRUD controllers arrive in a later task.</summary>
[ApiController]
[Authorize]
[Route("api/v1/whoami")]
public sealed class WhoAmIController : ControllerBase
{
    private readonly ICurrentUser _currentUser;

    public WhoAmIController(ICurrentUser currentUser) => _currentUser = currentUser;

    public sealed record WhoAmIResponse(Guid? UserId, string? Role);

    [HttpGet]
    public ActionResult<WhoAmIResponse> Get() =>
        Ok(new WhoAmIResponse(_currentUser.UserId, _currentUser.Role?.ToString()));
}
