using ExecPlan.Application.Auth;
using ExecPlan.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>Anonymous login/refresh endpoints over the shared <see cref="IAuthService"/>. <see cref="AppException"/>
/// with <see cref="AppException.Kind.Unauthorized"/> maps to HTTP 401; any other <see cref="AppException"/> kind is
/// not expected from this service and is left to bubble (later tasks may add a global exception filter).</summary>
[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService) => _authService = authService;

    public sealed record LoginRequest(string UserName, string Password);

    public sealed record RefreshRequest(string RefreshToken);

    [HttpPost("login")]
    public async Task<ActionResult<TokenPair>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        try
        {
            var tokens = await _authService.LoginAsync(request.UserName, request.Password, ct);
            return Ok(tokens);
        }
        catch (AppException ex) when (ex.ErrorKind == AppException.Kind.Unauthorized)
        {
            return Unauthorized(new { message = ex.Message, code = ex.Code });
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<TokenPair>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        try
        {
            var tokens = await _authService.RefreshAsync(request.RefreshToken, ct);
            return Ok(tokens);
        }
        catch (AppException ex) when (ex.ErrorKind == AppException.Kind.Unauthorized)
        {
            return Unauthorized(new { message = ex.Message, code = ex.Code });
        }
    }
}
