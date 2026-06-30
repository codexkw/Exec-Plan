using ExecPlan.Domain.Common;
using ExecPlan.Domain.Enums;

namespace ExecPlan.Domain.Entities;

public class ExecutionTask : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid ParticipantId { get; set; }
    public string Title { get; set; } = "";
    public int Order { get; set; }
    public ExecTaskStatus Status { get; set; } = ExecTaskStatus.Pending;
    public string? Note { get; set; }
    public DateTime DueAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Guid SourceTaskTemplateId { get; set; }
}
