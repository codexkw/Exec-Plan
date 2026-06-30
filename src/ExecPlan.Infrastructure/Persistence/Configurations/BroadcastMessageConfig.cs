using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class BroadcastMessageConfig : IEntityTypeConfiguration<BroadcastMessage>
{
    public void Configure(EntityTypeBuilder<BroadcastMessage> e)
    {
        e.ToTable("BroadcastMessages");
        e.HasKey(x => x.Id);
        e.Property(x => x.Body).IsRequired().HasMaxLength(2000);
        e.HasIndex(x => x.ActivationId);
    }
}
