using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class DepartmentConfig : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> e)
    {
        e.ToTable("Departments");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.OrganizationId);
    }
}
