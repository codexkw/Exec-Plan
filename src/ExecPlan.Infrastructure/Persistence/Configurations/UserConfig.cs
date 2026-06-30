using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class UserConfig : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> e)
    {
        e.ToTable("Users");
        e.HasKey(x => x.Id);
        e.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        e.HasIndex(x => x.UserName).IsUnique();
        e.Property(x => x.FullName).IsRequired().HasMaxLength(200);
        e.Property(x => x.Phone).HasMaxLength(40);
        e.Property(x => x.PasswordHash).IsRequired();
        e.HasIndex(x => x.OrganizationId);
        e.HasIndex(x => x.DepartmentId);
    }
}
