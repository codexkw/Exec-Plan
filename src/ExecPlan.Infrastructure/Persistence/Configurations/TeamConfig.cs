using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class TeamConfig : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> e)
    {
        e.ToTable("Teams");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.PlanId);
    }
}
