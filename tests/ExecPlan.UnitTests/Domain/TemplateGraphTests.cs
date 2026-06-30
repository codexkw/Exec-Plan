using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace ExecPlan.UnitTests.Domain;

public class TemplateGraphTests
{
    [Fact]
    public void Can_build_plan_with_team_member_task_and_roster()
    {
        var org = new Organization { Name = "Municipality" };
        var dept = new Department { Name = "Ops", OrganizationId = org.Id };
        var user = new User { UserName = "m1", FullName = "Member One", Phone = "+965", Role = UserRole.TeamMember, OrganizationId = org.Id, DepartmentId = dept.Id, PasswordHash = "x" };
        var plan = new Plan { Name = "Storm", Type = PlanType.Emergency, Objective = "o", Description = "d", Scope = "s", Status = PlanStatus.Ready, CreatedByUserId = user.Id };
        var team = new Team { PlanId = plan.Id, Name = "Alpha", TeamLeaderUserId = null };
        var tt = new TaskTemplate { TeamId = team.Id, Title = "Inspect", Order = 1, Duration = TimeSpan.FromMinutes(30) };
        var sa = new ShiftAssignment { TeamId = team.Id, UserId = user.Id, Shift = ShiftBand.Morning, Date = new DateTime(2026, 6, 30), SubstituteForUserId = null };

        plan.Id.Should().NotBe(Guid.Empty);
        tt.Duration.Should().Be(TimeSpan.FromMinutes(30));
        sa.SubstituteForUserId.Should().BeNull();
    }
}
