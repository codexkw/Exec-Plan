using ExecPlan.Domain.Common;

namespace ExecPlan.Domain.Entities;

public class ResponseStatus : BaseEntity
{
    public Guid ActivationId { get; set; }
    public Guid ParticipantId { get; set; }
    public DateTime AcknowledgedAtUtc { get; set; }
}
