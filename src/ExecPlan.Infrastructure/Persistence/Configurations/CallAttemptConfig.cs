using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class CallAttemptConfig : IEntityTypeConfiguration<CallAttempt>
{
    public void Configure(EntityTypeBuilder<CallAttempt> e)
    {
        e.ToTable("CallAttempts");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.ActivationId);
        e.HasIndex(x => x.ParticipantId);
    }
}
