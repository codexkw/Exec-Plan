using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class PlanConfig : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> e)
    {
        e.ToTable("Plans");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        e.Property(x => x.Objective).IsRequired().HasMaxLength(2000);
        e.Property(x => x.Description).IsRequired().HasMaxLength(2000);
        e.Property(x => x.Scope).IsRequired().HasMaxLength(2000);
        e.HasIndex(x => x.CreatedByUserId);
        e.HasIndex(x => x.Status);

        e.HasMany(x => x.Contacts)
            .WithOne()
            .HasForeignKey(c => c.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        e.HasMany(x => x.Activators)
            .WithOne()
            .HasForeignKey(a => a.PlanId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
