using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class TaskTemplateConfig : IEntityTypeConfiguration<TaskTemplate>
{
    public void Configure(EntityTypeBuilder<TaskTemplate> e)
    {
        e.ToTable("TaskTemplates");
        e.HasKey(x => x.Id);
        e.Property(x => x.Title).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.TeamId);
    }
}
