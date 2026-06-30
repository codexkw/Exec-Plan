using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class TeamMembershipConfig : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> e)
    {
        e.ToTable("TeamMemberships");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.TeamId);
        e.HasIndex(x => x.UserId);
    }
}
