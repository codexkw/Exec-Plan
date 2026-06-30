using ExecPlan.Api.Auth;
using ExecPlan.Application.Abstractions;
using ExecPlan.Application.Activation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>
/// The one-tap activation endpoint (PRD §5: Create → <b>Activate</b> → Notify → Execute → Dashboard).
/// Manager/Admin gated at the policy layer; the service additionally enforces creator/activator/admin
/// authorization and the "only one Active activation per plan" guard. The acting user comes from
/// <see cref="ICurrentUser"/> (401 if no authenticated principal). Thin: delegates to
/// <see cref="IActivationService"/> and returns the new activation id.
/// </summary>
[ApiController]
[Route("api/v1/plans")]
public sealed class PlansActivateController : ControllerBase
{
    private readonly IActivationService _activation;
    private readonly ICurrentUser _cur;

    public PlansActivateController(IActivationService activation, ICurrentUser cur)
    {
        _activation = activation;
        _cur = cur;
    }

    public sealed record ActivateResponse(Guid ActivationId);

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<ActionResult<ActivateResponse>> Activate(Guid id, CancellationToken ct)
    {
        if (_cur.UserId is not Guid actingUserId)
        {
            return Unauthorized();
        }

        var activationId = await _activation.ActivateAsync(id, actingUserId, ct);
        return Ok(new ActivateResponse(activationId));
    }
}
