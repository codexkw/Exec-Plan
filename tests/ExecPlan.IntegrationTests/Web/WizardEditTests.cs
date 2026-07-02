using System.Net;
using ExecPlan.Application.Auth;
using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ExecPlan.IntegrationTests.Web;

/// <summary>
/// Post-Ready EDIT flow for the create-plan wizard: an existing plan (Draft or Ready) can be re-opened
/// through all four steps to edit its info, add/remove teams/members/tasks, and replace its shift roster —
/// plus the roster step's "current Kuwait shift" hint. Anchors the operationally-important case: a plan
/// whose roster no longer matches the current shift ("No one is on duty for this shift.") can be fixed by
/// editing the roster to the current shift, after which it launches. Seeds its own Manager (+ a second,
/// non-owning Manager) + Organization + two members, idempotently, the same pattern the other wizard
/// test classes use. <see cref="TestAppFactory.FixedShift"/> is the deterministic shift the host clock
/// resolves, so a roster aligned to it activates.
/// </summary>
[Collection("WebHostSequential")]
public class WizardEditTests : IClassFixture<TestAppFactory>
{
    private const string ManagerUserName = "wizard-edit-manager";
    private const string OtherManagerUserName = "wizard-edit-other-manager";
    private const string Password = "Passw0rd!";

    private readonly TestAppFactory _factory;
    private Guid _orgId;
    private Guid _managerId;
    private Guid _otherManagerId;
    private Guid _member1Id;
    private Guid _member2Id;

    public WizardEditTests(TestAppFactory factory)
    {
        _factory = factory;
        EnsureSeeded();
    }

    private void EnsureSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var org = ctx.Organizations.FirstOrDefault(o => o.Name == "WizardEdit Test Org");
        if (org is null)
        {
            org = new Organization { Name = "WizardEdit Test Org" };
            ctx.Organizations.Add(org);
            ctx.SaveChanges();
        }

        _orgId = org.Id;
        _managerId = EnsureUser(ctx, hasher, ManagerUserName, "Wizard Edit Manager", "+96500000901", UserRole.PlanManager);
        _otherManagerId = EnsureUser(ctx, hasher, OtherManagerUserName, "Wizard Edit Other Manager", "+96500000902", UserRole.PlanManager);
        _member1Id = EnsureUser(ctx, hasher, "wizard-edit-member1", "Wizard Edit Member 1", "+96500000903", UserRole.TeamMember);
        _member2Id = EnsureUser(ctx, hasher, "wizard-edit-member2", "Wizard Edit Member 2", "+96500000904", UserRole.TeamMember);
    }

    private Guid EnsureUser(ExecPlanDbContext ctx, IPasswordHasher hasher, string userName, string fullName, string phone, UserRole role)
    {
        var user = ctx.Users.FirstOrDefault(u => u.UserName == userName);
        if (user is null)
        {
            user = new User
            {
                UserName = userName,
                PasswordHash = hasher.Hash(Password),
                FullName = fullName,
                Phone = phone,
                Role = role,
                OrganizationId = _orgId,
                IsActive = true,
            };
            ctx.Users.Add(user);
            ctx.SaveChanges();
        }

        return user.Id;
    }

    private sealed record Seeded(Guid PlanId, Guid TeamId, Guid TaskId);

    /// <summary>Seeds a READY plan owned by the manager: one team, both members, one task template, and a
    /// primary (non-substitute) ShiftAssignment per member at <paramref name="shift"/>/<paramref name="date"/>.</summary>
    private Seeded SeedReadyPlan(string planName, string teamName, ShiftBand shift, DateTime date)
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();

        var plan = new Plan { Name = planName, Type = PlanType.Daily, Status = PlanStatus.Ready, CreatedByUserId = _managerId };
        ctx.Plans.Add(plan);
        ctx.SaveChanges();

        var team = new Team { PlanId = plan.Id, Name = teamName };
        ctx.Teams.Add(team);
        ctx.SaveChanges();

        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member1Id });
        ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member2Id });
        var task = new TaskTemplate { TeamId = team.Id, Title = "Close affected roads", Order = 1, Duration = TimeSpan.FromMinutes(30) };
        ctx.TaskTemplates.Add(task);
        ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = _member1Id, Shift = shift, Date = date });
        ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = _member2Id, Shift = shift, Date = date });
        ctx.SaveChanges();

        return new Seeded(plan.Id, team.Id, task.Id);
    }

    private static List<KeyValuePair<string, string>> RosterFields(Guid teamId, Guid m1, Guid m2, ShiftBand shift, DateTime date)
        => new()
        {
            new("roster[0].TeamId", teamId.ToString()),
            new("roster[0].UserId", m1.ToString()),
            new("roster[0].Shift", shift.ToString()),
            new("roster[0].Date", date.ToString("yyyy-MM-dd")),
            new("roster[1].TeamId", teamId.ToString()),
            new("roster[1].UserId", m2.ToString()),
            new("roster[1].Shift", shift.ToString()),
            new("roster[1].Date", date.ToString("yyyy-MM-dd")),
        };

    [Fact]
    public async Task Ready_plan_is_editable_across_steps()
    {
        // The old guard redirected any non-Draft plan away from the wizard; the edit flow must let a Ready
        // plan into every step instead of bouncing to detail.
        var s = SeedReadyPlan("WizardEdit Editable Plan", "Alpha", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        foreach (var step in new[] { "info", "teams", "tasks", "review" })
        {
            var res = await client.GetAsync($"/admin/plans/create/{s.PlanId}/{step}");
            res.StatusCode.Should().Be(HttpStatusCode.OK, $"step {step} should render for a Ready plan");
        }
    }

    [Fact]
    public async Task EditInfo_updates_plan_in_place()
    {
        var s = SeedReadyPlan("WizardEdit Rename Before", "Bravo", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{s.PlanId}/info";
        var res = await WebTestHelpers.PostFormAsync(client, url, url, new Dictionary<string, string>
        {
            ["Name"] = "WizardEdit Rename After",
            ["Type"] = nameof(PlanType.Emergency),
            ["Objective"] = "Updated objective",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be($"/admin/plans/create/{s.PlanId}/teams");

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var plan = ctx.Plans.First(p => p.Id == s.PlanId);
        plan.Name.Should().Be("WizardEdit Rename After");
        plan.Type.Should().Be(PlanType.Emergency);
        plan.Objective.Should().Be("Updated objective");
        plan.Status.Should().Be(PlanStatus.Ready); // editing info never changes status
    }

    [Fact]
    public async Task Remove_team_cascades_members_tasks_and_roster()
    {
        var s = SeedReadyPlan("WizardEdit Remove Team Plan", "Charlie", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{s.PlanId}/teams";
        var res = await WebTestHelpers.PostFormAsync(client, url, url, new Dictionary<string, string>
        {
            ["intent"] = "remove-team",
            ["teamId"] = s.TeamId.ToString(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK); // re-render of step 2

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        ctx.Teams.Where(t => t.Id == s.TeamId).Should().BeEmpty();
        ctx.TeamMemberships.Where(m => m.TeamId == s.TeamId).Should().BeEmpty();
        ctx.TaskTemplates.Where(t => t.TeamId == s.TeamId).Should().BeEmpty();
        ctx.ShiftAssignments.Where(a => a.TeamId == s.TeamId).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_member_drops_membership_and_that_members_roster_only()
    {
        var s = SeedReadyPlan("WizardEdit Remove Member Plan", "Delta", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{s.PlanId}/teams";
        var res = await WebTestHelpers.PostFormAsync(client, url, url, new Dictionary<string, string>
        {
            ["intent"] = "remove-member",
            ["teamId"] = s.TeamId.ToString(),
            ["userId"] = _member2Id.ToString(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        ctx.TeamMemberships.Where(m => m.TeamId == s.TeamId && m.UserId == _member2Id).Should().BeEmpty();
        ctx.ShiftAssignments.Where(a => a.TeamId == s.TeamId && a.UserId == _member2Id).Should().BeEmpty();
        // member1 untouched
        ctx.TeamMemberships.Where(m => m.TeamId == s.TeamId && m.UserId == _member1Id).Should().HaveCount(1);
        ctx.ShiftAssignments.Where(a => a.TeamId == s.TeamId && a.UserId == _member1Id).Should().HaveCount(1);
    }

    [Fact]
    public async Task Remove_task_removes_only_that_task()
    {
        var s = SeedReadyPlan("WizardEdit Remove Task Plan", "Echo", ShiftBand.Morning, new DateTime(2026, 6, 30));

        Guid keptTaskId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var kept = new TaskTemplate { TeamId = s.TeamId, Title = "Kept task", Order = 2, Duration = TimeSpan.FromMinutes(15) };
            ctx.TaskTemplates.Add(kept);
            ctx.SaveChanges();
            keptTaskId = kept.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{s.PlanId}/tasks";
        var res = await WebTestHelpers.PostFormAsync(client, url, url, new Dictionary<string, string>
        {
            ["intent"] = "remove-task",
            ["taskId"] = s.TaskId.ToString(),
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = _factory.Services.CreateScope();
        var ctx2 = scope2.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        ctx2.TaskTemplates.Where(t => t.Id == s.TaskId).Should().BeEmpty();
        ctx2.TaskTemplates.Where(t => t.Id == keptTaskId).Should().HaveCount(1);
    }

    [Fact]
    public async Task Refinishing_replaces_the_roster_instead_of_appending()
    {
        // Seed roster at Morning/2026-06-29; re-finish at Evening/2026-06-30 → exactly the two new rows,
        // no duplicates left over from the original roster.
        var s = SeedReadyPlan("WizardEdit Replace Roster Plan", "Foxtrot", ShiftBand.Morning, new DateTime(2026, 6, 29));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{s.PlanId}/review";
        var res = await WebTestHelpers.PostFormAsync(client, url, url,
            RosterFields(s.TeamId, _member1Id, _member2Id, ShiftBand.Evening, new DateTime(2026, 6, 30)));

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        var rows = ctx.ShiftAssignments.Where(a => a.TeamId == s.TeamId).ToList();
        rows.Should().HaveCount(2); // replaced, not 4
        rows.Should().OnlyContain(a => a.Shift == ShiftBand.Evening && a.Date == new DateTime(2026, 6, 30));
    }

    [Fact]
    public async Task Editing_roster_to_the_current_shift_makes_a_stuck_plan_launchable()
    {
        // The operational crux. Seed a Ready plan whose roster is for a date that is NOT the host's
        // resolved roster date, so activation fails with "No one is on duty for this shift." Then edit the
        // roster to the current (FixedShift) band+date and launch — now it activates.
        var fixedShift = _factory.FixedShift;
        var wrongDate = fixedShift.RosterDate.AddDays(-2);
        var s = SeedReadyPlan("WizardEdit Stuck Plan", "Golf", fixedShift.Band, wrongDate);

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        // 1. Activation fails while the roster is stale (bounces back to detail, plan not activated).
        var detailUrl = $"/admin/plans/{s.PlanId}";
        var failed = await WebTestHelpers.PostFormAsync(client, detailUrl, $"/admin/plans/{s.PlanId}/activate",
            new Dictionary<string, string>());
        failed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        failed.Headers.Location!.ToString().Should().Be(detailUrl);

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            ctx.PlanActivations.Where(a => a.PlanId == s.PlanId).Should().BeEmpty();
        }

        // 2. Edit the roster to the current Kuwait shift + date.
        var reviewUrl = $"/admin/plans/create/{s.PlanId}/review";
        var fixedRes = await WebTestHelpers.PostFormAsync(client, reviewUrl, reviewUrl,
            RosterFields(s.TeamId, _member1Id, _member2Id, fixedShift.Band, fixedShift.RosterDate));
        fixedRes.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // 3. Launch now succeeds → redirect to the live dashboard, and an Active activation exists.
        var ok = await WebTestHelpers.PostFormAsync(client, detailUrl, $"/admin/plans/{s.PlanId}/activate",
            new Dictionary<string, string>());
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ok.Headers.Location!.ToString().Should().StartWith("/admin/activations/");

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            ctx.PlanActivations.Where(a => a.PlanId == s.PlanId && a.Status == ActivationStatus.Active).Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task Review_shows_current_kuwait_shift_hint_and_step_links()
    {
        var s = SeedReadyPlan("WizardEdit Hint Plan", "Hotel", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var body = await client.GetStringAsync($"/admin/plans/create/{s.PlanId}/review");

        // Current-shift hint carries the host's resolved band/date.
        body.Should().Contain("id=\"current-shift-hint\"");
        body.Should().Contain($"data-shift=\"{_factory.FixedShift.Band}\"");
        body.Should().Contain($"data-date=\"{_factory.FixedShift.RosterDate:yyyy-MM-dd}\"");
        body.Should().Contain("id=\"set-all-current\"");
        // Steps are navigable links when editing an existing plan.
        body.Should().Contain($"href=\"/admin/plans/create/{s.PlanId}/info\"");
    }

    [Fact]
    public async Task Detail_shows_edit_button_for_the_owner()
    {
        var s = SeedReadyPlan("WizardEdit Detail Button Plan", "India", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var body = await client.GetStringAsync($"/admin/plans/{s.PlanId}");
        body.Should().Contain($"/admin/plans/create/{s.PlanId}/info");
    }

    [Fact]
    public async Task A_non_owning_manager_cannot_edit_someone_elses_plan()
    {
        var s = SeedReadyPlan("WizardEdit Foreign Plan", "Juliet", ShiftBand.Morning, new DateTime(2026, 6, 30));

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, OtherManagerUserName, Password);

        var res = await client.GetAsync($"/admin/plans/create/{s.PlanId}/info");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("/admin/denied");
    }

    [Fact]
    public async Task Review_get_preserves_an_existing_substitute_assignment()
    {
        // member1 is on duty (primary row); member2 stands in FOR member1 (a substitute row). Re-opening the
        // roster editor must round-trip that substitute link — Finish is a full REPLACE, so a row seeded with
        // SubstituteForUserId dropped would silently delete the substitute (and disable escalation-induction).
        Guid planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var plan = new Plan { Name = "WizardEdit Substitute Plan", Type = PlanType.Daily, Status = PlanStatus.Ready, CreatedByUserId = _managerId };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();
            var team = new Team { PlanId = plan.Id, Name = "Kilo" };
            ctx.Teams.Add(team);
            ctx.SaveChanges();
            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member1Id });
            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member2Id });
            ctx.TaskTemplates.Add(new TaskTemplate { TeamId = team.Id, Title = "T", Order = 1, Duration = TimeSpan.FromMinutes(10) });
            ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = _member1Id, Shift = ShiftBand.Morning, Date = new DateTime(2026, 6, 30) });
            ctx.ShiftAssignments.Add(new ShiftAssignment { TeamId = team.Id, UserId = _member2Id, Shift = ShiftBand.Morning, Date = new DateTime(2026, 6, 30), SubstituteForUserId = _member1Id });
            ctx.SaveChanges();
            planId = plan.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var body = await client.GetStringAsync($"/admin/plans/create/{planId}/review");
        // The only place a member guid appears with selected="selected" is member2's "substitute for" select
        // showing member1 — proving SubstituteForUserId round-tripped (Shift selects use enum-name values).
        body.Should().Contain($"value=\"{_member1Id}\" selected=\"selected\"");
    }

    [Fact]
    public async Task Editing_is_blocked_while_an_activation_is_active()
    {
        var fixedShift = _factory.FixedShift;
        var s = SeedReadyPlan("WizardEdit Active Plan", "Lima", fixedShift.Band, fixedShift.RosterDate);
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            ctx.PlanActivations.Add(new PlanActivation
            {
                PlanId = s.PlanId,
                Status = ActivationStatus.Active,
                Shift = fixedShift.Band,
                RosterDate = fixedShift.RosterDate,
                ActivatedByUserId = _managerId,
                ActivatedAtUtc = new DateTime(2026, 6, 30, 8, 0, 0, DateTimeKind.Utc),
                EscalationThreshold = 5,
            });
            ctx.SaveChanges();
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        // Server-side guard: any edit step redirects back to Detail instead of mutating a running plan.
        var res = await client.GetAsync($"/admin/plans/create/{s.PlanId}/teams");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Be($"/admin/plans/{s.PlanId}");

        // And Detail hides the "Edit plan" entry point while the plan is active.
        var detail = await client.GetStringAsync($"/admin/plans/{s.PlanId}");
        detail.Should().NotContain($"/admin/plans/create/{s.PlanId}/info");
    }

    [Fact]
    public async Task Removing_the_team_leader_clears_the_leader_reference()
    {
        // member2 is both a member AND the team leader; removing them must clear Team.TeamLeaderUserId so
        // they don't retain leader-level authority (dashboard viewing, reassignment) over the team.
        Guid planId, teamId;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
            var plan = new Plan { Name = "WizardEdit Leader Plan", Type = PlanType.Daily, Status = PlanStatus.Ready, CreatedByUserId = _managerId };
            ctx.Plans.Add(plan);
            ctx.SaveChanges();
            var team = new Team { PlanId = plan.Id, Name = "Mike", TeamLeaderUserId = _member2Id };
            ctx.Teams.Add(team);
            ctx.SaveChanges();
            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member1Id });
            ctx.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = _member2Id });
            ctx.SaveChanges();
            planId = plan.Id;
            teamId = team.Id;
        }

        var client = WebTestHelpers.NewClient(_factory);
        await WebTestHelpers.LoginAsync(client, ManagerUserName, Password);

        var url = $"/admin/plans/create/{planId}/teams";
        var res = await WebTestHelpers.PostFormAsync(client, url, url, new Dictionary<string, string>
        {
            ["intent"] = "remove-member",
            ["teamId"] = teamId.ToString(),
            ["userId"] = _member2Id.ToString(),
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope2 = _factory.Services.CreateScope();
        var ctx2 = scope2.ServiceProvider.GetRequiredService<ExecPlanDbContext>();
        ctx2.Teams.First(t => t.Id == teamId).TeamLeaderUserId.Should().BeNull();
    }
}
