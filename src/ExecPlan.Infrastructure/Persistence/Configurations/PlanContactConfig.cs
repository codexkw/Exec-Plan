using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class PlanContactConfig : IEntityTypeConfiguration<PlanContact>
{
    public void Configure(EntityTypeBuilder<PlanContact> e)
    {
        e.ToTable("PlanContacts");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        e.Property(x => x.Number).IsRequired().HasMaxLength(40);
        e.HasIndex(x => x.PlanId);
    }
}
