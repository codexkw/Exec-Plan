using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class ExecutionTaskConfig : IEntityTypeConfiguration<ExecutionTask>
{
    public void Configure(EntityTypeBuilder<ExecutionTask> e)
    {
        e.ToTable("ExecutionTasks");
        e.HasKey(x => x.Id);
        e.Property(x => x.Title).IsRequired().HasMaxLength(200);
        e.Property(x => x.Note).HasMaxLength(2000);
        e.HasIndex(x => new { x.ActivationId, x.ParticipantId });
    }
}
