using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ExecPlan.Api.Hubs;

/// <summary>
/// The in-process live-dashboard hub (design §5.5). Clients connect with a JWT (carried on the
/// <c>access_token</c> query string during the WebSocket handshake — see <c>Program.cs</c>'s
/// <c>JwtBearerEvents.OnMessageReceived</c>) and call <see cref="JoinActivation"/> to subscribe to the
/// per-activation group <c>act-{id}</c>. <see cref="SignalRRealtimeNotifier"/> pushes
/// <c>DashboardUpdated</c>/<c>ActivationClosed</c> messages to that group after each committed state
/// change.
///
/// <para><b>Object-level visibility:</b> <see cref="JoinActivation"/> mirrors the REST dashboard gate
/// (<see cref="ExecPlan.Api.Controllers.ActivationsController.Dashboard"/>, DEC-17): Manager/Admin see
/// any activation; otherwise the caller must be a participant of the activation OR a TeamLeader of a
/// team participating in it — else a <see cref="HubException"/> is thrown and the connection is NOT
/// added to the group.</para>
///
/// <para>Identity is read from <see cref="HubCallerContext.User"/> (the authenticated principal on the
/// connection) rather than the request-scoped <c>ICurrentUser</c>/<c>IHttpContextAccessor</c>, which is
/// not reliably populated during hub method invocations. The claim types match exactly what
/// <c>JwtTokenFactory</c> emits and <c>CurrentUser</c> reads (<c>sub</c> + <c>ClaimTypes.Role</c>).</para>
/// </summary>
[Authorize]
public sealed class DashboardHub : Hub
{
    private readonly IUnitOfWork _uow;

    public DashboardHub(IUnitOfWork uow) => _uow = uow;

    /// <summary>Subscribe the calling connection to live updates for <paramref name="activationId"/>
    /// (after the visibility check). Throws <see cref="HubException"/> if the caller may not view it.</summary>
    public async Task JoinActivation(Guid activationId)
    {
        if (!await CanViewAsync(activationId))
        {
            throw new HubException("You are not allowed to view this activation.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(activationId));
    }

    /// <summary>Unsubscribe the calling connection from <paramref name="activationId"/>'s group.</summary>
    public Task LeaveActivation(Guid activationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(activationId));

    internal static string GroupName(Guid activationId) => $"act-{activationId}";

    private Guid? CallerUserId =>
        Guid.TryParse(Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id) ? id : null;

    private UserRole? CallerRole =>
        Enum.TryParse<UserRole>(Context.User?.FindFirst(ClaimTypes.Role)?.Value, out var role) ? role : null;

    private async Task<bool> CanViewAsync(Guid activationId)
    {
        var role = CallerRole;

        // Manager/Admin see any activation.
        if (role is UserRole.SystemAdmin or UserRole.PlanManager)
        {
            return true;
        }

        if (CallerUserId is not Guid userId)
        {
            return false;
        }

        var participants = await _uow.Repo<ActivationParticipant>()
            .ListAsync(p => p.ActivationId == activationId, Context.ConnectionAborted);

        // A participant of the activation may view it.
        if (participants.Any(p => p.UserId == userId))
        {
            return true;
        }

        // A TeamLeader may view an activation in which a team they lead participates (DEC-17).
        if (role == UserRole.TeamLeader)
        {
            var teamIds = participants.Select(p => p.TeamId).Distinct().ToList();
            var teams = await _uow.Repo<Team>()
                .ListAsync(t => teamIds.Contains(t.Id), Context.ConnectionAborted);
            if (teams.Any(t => t.TeamLeaderUserId == userId))
            {
                return true;
            }
        }

        return false;
    }
}
