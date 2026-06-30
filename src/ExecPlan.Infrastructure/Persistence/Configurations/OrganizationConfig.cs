using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class OrganizationConfig : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> e)
    {
        e.ToTable("Organizations");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
    }
}
