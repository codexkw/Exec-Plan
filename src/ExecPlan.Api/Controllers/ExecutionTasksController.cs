using ExecPlan.Application.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExecPlan.Api.Controllers;

/// <summary>
/// Live edits to a single execution task (design §5.6): toggle done, set a note, and/or reassign it to
/// another participant. Authenticated only at the policy layer; all object-level authorization (owner /
/// source-team leader / manager-admin, and the cross-team reassign rules) is enforced inside
/// <see cref="ExecutionService.UpdateTaskAsync"/>. Thin: validates the route id, delegates, returns 200.
/// </summary>
[ApiController]
[Route("api/v1/execution-tasks")]
public sealed class ExecutionTasksController : ControllerBase
{
    private readonly ExecutionService _execution;

    public ExecutionTasksController(ExecutionService execution) => _execution = execution;

    public sealed record UpdateTaskRequest(bool? Done, string? Note, Guid? ReassignToParticipantId);

    [HttpPatch("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTaskRequest request, CancellationToken ct)
    {
        await _execution.UpdateTaskAsync(id, request.Done, request.Note, request.ReassignToParticipantId, ct);
        return Ok();
    }
}
