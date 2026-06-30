using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class PlanActivatorConfig : IEntityTypeConfiguration<PlanActivator>
{
    public void Configure(EntityTypeBuilder<PlanActivator> e)
    {
        e.ToTable("PlanActivators");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.PlanId);
        e.HasIndex(x => x.UserId);
    }
}
