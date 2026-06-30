using ExecPlan.Domain.Entities;
using ExecPlan.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace ExecPlan.UnitTests.Domain;

public class RuntimeGraphTests
{
    [Fact]
    public void Activation_participant_and_task_link_by_ids()
    {
        var act = new PlanActivation { PlanId = Guid.NewGuid(), Status = ActivationStatus.Active, Shift = ShiftBand.Morning, RosterDate = new DateTime(2026, 6, 30), ActivatedByUserId = Guid.NewGuid(), ActivatedAtUtc = DateTime.UtcNow, EscalationThreshold = 5 };
        var p = new ActivationParticipant { ActivationId = act.Id, UserId = Guid.NewGuid(), TeamId = Guid.NewGuid(), TeamNameSnapshot = "Alpha", Status = ParticipantStatus.Pending, CallAttemptCount = 0 };
        var t = new ExecutionTask { ActivationId = act.Id, ParticipantId = p.Id, Title = "Inspect", Order = 1, Status = ExecTaskStatus.Pending, DueAtUtc = DateTime.UtcNow.AddMinutes(30) };

        t.ParticipantId.Should().Be(p.Id);
        act.EscalationThreshold.Should().Be(5);
    }
}
