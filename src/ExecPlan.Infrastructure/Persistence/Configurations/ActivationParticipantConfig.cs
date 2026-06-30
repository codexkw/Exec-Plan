using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class ActivationParticipantConfig : IEntityTypeConfiguration<ActivationParticipant>
{
    public void Configure(EntityTypeBuilder<ActivationParticipant> e)
    {
        e.ToTable("ActivationParticipants");
        e.HasKey(x => x.Id);
        e.Property(x => x.TeamNameSnapshot).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.ActivationId);
    }
}
