using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class EscalationLogConfig : IEntityTypeConfiguration<EscalationLog>
{
    public void Configure(EntityTypeBuilder<EscalationLog> e)
    {
        e.ToTable("EscalationLogs");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.ActivationId);
        e.HasIndex(x => x.ParticipantId);
    }
}
