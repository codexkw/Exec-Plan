using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests;

/// <summary>
/// The headline PRD §21 acceptance test: the entire activation cycle exercised end-to-end over real
/// HTTP against the hosted API (<see cref="TestAppFactory"/>). Seeds a plan with two on-duty members
/// and a frozen substitute aligned to the host's deterministic Kuwait shift, then drives
/// Activate → Acknowledge → Dashboard → Escalate-to-induction → member-scoped visibility → Close,
/// asserting both HTTP status codes and parsed JSON at every step. This is the integration proof that
/// the controllers + services + error middleware all wire together.
/// </summary>
public class AcceptanceFlowTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public AcceptanceFlowTests(TestAppFactory factory) => _factory = factory;

    private const string Password = "Accept-Pass-w0rd-123";

    private sealed record TokenPairDto(string AccessToken, string RefreshToken, DateTime AccessExpiresUtc, Guid UserId, UserRole Role, string FullName);

    private sealed record ActivateResponseDto(Guid ActivationId);

    private sealed record DashboardSnapshot(
        Guid ActivationId, ActivationStatus Status, int TotalParticipants, int PendingCount, int ReadyCount, int EscalatedCount, int InductedCount);

    private sealed record EscalationResultDto(int AttemptsAdded, int Inducted);

    private sealed record MyTaskDto(Guid Id, Guid ActivationId, Guid ParticipantId, string Title, int Order);

    private sealed record NotificationDto(Guid Id, NotificationKind Kind, string Body, DateTime CreatedAtUtc);

    private sealed record Seed(
        Guid PlanId, string ManagerUserName, string Member1UserName, string Member2UserName,
        Guid Member1UserId, Guid Member2UserId, Guid SubstituteUserId, Guid TeamId);

    private async Task<HttpClient> LoginAsAsync(string userName, string password)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<TokenPairDto>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return client;
    }

    /// <summary>
    /// Seeds the §21 scenario directly through a DbContext scope on the same shared in-memory DB the
    /// host serves: an org, a manager (plan creator), two on-duty members, one substitute standing in
    /// for member2, a plan + team, two task templates, and the three roster rows aligned to the host
    /// clock's resolved <see cref="ShiftResolution"/>. All passwords go through the real hasher.
    /// </summary>
    private Seed SeedScenario()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var ctx = sp.GetRequiredService<ExecPlanDbContext>();
        var hasher = sp.GetRequiredService<IPasswordHasher>();
        var shift = _factory.FixedShift;

        var org = new Organization { Name = $"Accept-Org-{Guid.NewGuid():N}" };
        ctx.Organizations.Add(org);

        User MakeUser(UserRole role, string label) => new()
        {
            UserName = $"{label}-{Guid.NewGuid():N}",
            FullName = label,
            Phone = "+96500000000",
            PasswordHash = hasher.Hash(Password),
            Role = role,
            OrganizationId = org.Id,
            IsActive = true,
        };

        var manager = MakeUser(UserRole.PlanManager, "mgr");
        var member1 = MakeUser(UserRole.TeamMember, "m1");
        var member2 = MakeUser(UserRole.TeamMember, "m2");
        var substitute = MakeUser(UserRole.TeamMember, "sub");
        ctx.Users.AddRange(manager, member1, member2, substitute);

        var plan = new Plan
        {
            Name = "Acceptance Plan",
            Type = PlanType.Guard,
            Status = PlanStatus.Ready,
            CreatedByUserId = manager.Id,
        };
        ctx.Plans.Add(plan);

        var team = new Team { PlanId = plan.Id, Name = "Alpha" };
        ctx.Teams.Add(team);

        ctx.TaskTemplates.Add(new TaskTemplate { TeamId = team.Id, Title = "Task A", Order = 1, Duration = TimeSpan.FromMinutes(30) });
        ctx.TaskTemplates.Add(new TaskTemplate { TeamId = team.Id, Title = "Task B", Order = 2, Duration = TimeSpan.FromHours(2) });

        // Two on-duty members (SubstituteForUserId == null) plus one substitute row standing in for member2,
        // all on the host clock's resolved (Band, RosterDate).
        ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = member1.Id, Shift = shift.Band, Date = shift.RosterDate });
        ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = member2.Id, Shift = shift.Band, Date = shift.RosterDate });
        ctx.ShiftAssignments.Add(new ShiftAssignment
        {
            TeamId = team.Id,
            UserId = substitute.Id,
            Shift = shift.Band,
            Date = shift.RosterDate,
            SubstituteForUserId = member2.Id,
        });

        ctx.SaveChanges();

        return new Seed(plan.Id, manager.UserName, member1.UserName, member2.UserName, member1.Id, member2.Id, substitute.Id, team.Id);
    }

    [Fact]
    public async Task Full_activation_cycle_meets_PRD_section_21()
    {
        var seed = SeedScenario();

        // 2. Manager (plan creator) activates the plan.
        var manager = await LoginAsAsync(seed.ManagerUserName, Password);
        var activateResponse = await manager.PostAsync($"/api/v1/plans/{seed.PlanId}/activate", null);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var activation = await activateResponse.Content.ReadFromJsonAsync<ActivateResponseDto>();
        activation.Should().NotBeNull();
        var activationId = activation!.ActivationId;
        activationId.Should().NotBe(Guid.Empty);

        // 3. Member1 confirms readiness — the only counted response.
        var member1 = await LoginAsAsync(seed.Member1UserName, Password);
        var ackResponse = await member1.PostAsync($"/api/v1/activations/{activationId}/acknowledge", null);
        ackResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Dashboard: one Ready (member1), one Pending (member2).
        var dashResponse = await manager.GetAsync($"/api/v1/activations/{activationId}/dashboard");
        dashResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var dash = await dashResponse.Content.ReadFromJsonAsync<DashboardSnapshot>();
        dash.Should().NotBeNull();
        dash!.ReadyCount.Should().Be(1);
        dash.PendingCount.Should().Be(1);
        dash.TotalParticipants.Should().Be(2);

        // 5. Escalate repeatedly (bounded by the threshold) until member2 escalates and the substitute inducts.
        DashboardSnapshot? escalatedDash = null;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            var escResponse = await manager.PostAsync($"/api/v1/activations/{activationId}/run-escalation", null);
            escResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var escResult = await escResponse.Content.ReadFromJsonAsync<EscalationResultDto>();
            escResult.Should().NotBeNull();

            var pollResponse = await manager.GetAsync($"/api/v1/activations/{activationId}/dashboard");
            pollResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            escalatedDash = await pollResponse.Content.ReadFromJsonAsync<DashboardSnapshot>();
            if (escalatedDash!.EscalatedCount >= 1)
            {
                break;
            }
        }

        escalatedDash.Should().NotBeNull();
        escalatedDash!.EscalatedCount.Should().Be(1);
        escalatedDash.InductedCount.Should().Be(1);

        // Confirm via the store: member2 escalated AND the frozen substitute is now an Inducted participant.
        using (var verifyScope = _factory.Services.CreateScope())
        {
            var ctx = verifyScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var participants = ctx.ActivationParticipants.Where(p => p.ActivationId == activationId).ToList();

            var member2Participant = participants.Single(p => p.UserId == seed.Member2UserId && !p.IsSubstitute);
            member2Participant.Status.Should().Be(ParticipantStatus.Escalated);

            var sub = participants.Single(p => p.IsSubstitute);
            sub.Status.Should().Be(ParticipantStatus.Inducted);
            sub.UserId.Should().Be(seed.SubstituteUserId);
            sub.InductedFromParticipantId.Should().Be(member2Participant.Id);
        }

        // 6. Member-only visibility: member1's my-tasks returns ONLY member1's tasks (== template count).
        var myTasksResponse = await member1.GetAsync($"/api/v1/activations/{activationId}/my-tasks");
        myTasksResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var myTasks = await myTasksResponse.Content.ReadFromJsonAsync<List<MyTaskDto>>();
        myTasks.Should().NotBeNull();
        myTasks!.Should().HaveCount(2); // the team's two templates

        using (var visibilityScope = _factory.Services.CreateScope())
        {
            var ctx = visibilityScope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var member1Participant = ctx.ActivationParticipants.Single(p => p.ActivationId == activationId && p.UserId == seed.Member1UserId);
            var member2Participant = ctx.ActivationParticipants.Single(p => p.ActivationId == activationId && p.UserId == seed.Member2UserId && !p.IsSubstitute);
            var member2TaskIds = ctx.ExecutionTasks.Where(t => t.ParticipantId == member2Participant.Id).Select(t => t.Id).ToHashSet();

            myTasks!.Should().OnlyContain(t => t.ParticipantId == member1Participant.Id);
            myTasks!.Select(t => t.Id).Should().NotIntersectWith(member2TaskIds);
        }

        // 7. Manager closes the activation; the returned DashboardDto directly confirms Closed (DEC-17
        // restored DashboardDto.Status, so this no longer needs a DbContext-scope workaround).
        var closeResponse = await manager.PostAsync($"/api/v1/activations/{activationId}/close", null);
        closeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var closedDash = await closeResponse.Content.ReadFromJsonAsync<DashboardSnapshot>();
        closedDash.Should().NotBeNull();
        closedDash!.ActivationId.Should().Be(activationId);
        closedDash.Status.Should().Be(ActivationStatus.Closed);

        var afterCloseDash = await manager.GetAsync($"/api/v1/activations/{activationId}/dashboard");
        afterCloseDash.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterCloseSnapshot = await afterCloseDash.Content.ReadFromJsonAsync<DashboardSnapshot>();
        afterCloseSnapshot.Should().NotBeNull();
        afterCloseSnapshot!.Status.Should().Be(ActivationStatus.Closed);

        // Re-closing a closed activation is a Conflict — proves the AppException→409 middleware mapping end-to-end.
        var reCloseResponse = await manager.PostAsync($"/api/v1/activations/{activationId}/close", null);
        reCloseResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// my-notifications is per-caller (Task 18 follow-up, Minor gap): a broadcast stages one
    /// <see cref="NotificationLog"/> row per participant, addressed to that participant's own
    /// <c>RecipientUserId</c>. member1's GET must return member1's row and must NOT leak member2's row
    /// (and vice versa), even though both originate from the same broadcast body.
    /// </summary>
    [Fact]
    public async Task My_notifications_returns_only_the_callers_own_rows()
    {
        var seed = SeedScenario();

        var manager = await LoginAsAsync(seed.ManagerUserName, Password);
        var activateResponse = await manager.PostAsync($"/api/v1/plans/{seed.PlanId}/activate", null);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var activation = await activateResponse.Content.ReadFromJsonAsync<ActivateResponseDto>();
        var activationId = activation!.ActivationId;

        var broadcastResponse = await manager.PostAsJsonAsync(
            $"/api/v1/activations/{activationId}/broadcast", new { body = "Move to position" });
        broadcastResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var member1 = await LoginAsAsync(seed.Member1UserName, Password);
        var member1Response = await member1.GetAsync($"/api/v1/activations/{activationId}/my-notifications");
        member1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var member1Notifications = await member1Response.Content.ReadFromJsonAsync<List<NotificationDto>>();
        member1Notifications.Should().NotBeNull();
        member1Notifications!.Should().ContainSingle(n => n.Body == "Move to position" && n.Kind == NotificationKind.Broadcast);

        var member2 = await LoginAsAsync(seed.Member2UserName, Password);
        var member2Response = await member2.GetAsync($"/api/v1/activations/{activationId}/my-notifications");
        member2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var member2Notifications = await member2Response.Content.ReadFromJsonAsync<List<NotificationDto>>();
        member2Notifications.Should().NotBeNull();
        member2Notifications!.Should().ContainSingle(n => n.Body == "Move to position" && n.Kind == NotificationKind.Broadcast);

        // Isolation: distinct NotificationLog rows (one per participant) — neither list contains the other's.
        var member1Ids = member1Notifications!.Select(n => n.Id).ToHashSet();
        var member2Ids = member2Notifications!.Select(n => n.Id).ToHashSet();
        member1Ids.Should().NotIntersectWith(member2Ids);
    }
}
