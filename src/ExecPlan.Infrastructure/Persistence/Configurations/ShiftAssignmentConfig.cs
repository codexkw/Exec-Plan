using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class ShiftAssignmentConfig : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> e)
    {
        e.ToTable("ShiftAssignments");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.TeamId, x.Shift, x.Date });
    }
}
