using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class PlanActivationConfig : IEntityTypeConfiguration<PlanActivation>
{
    public void Configure(EntityTypeBuilder<PlanActivation> e)
    {
        e.ToTable("PlanActivations");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.PlanId, x.Status });
    }
}
