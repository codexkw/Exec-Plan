using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class ResponseStatusConfig : IEntityTypeConfiguration<ResponseStatus>
{
    public void Configure(EntityTypeBuilder<ResponseStatus> e)
    {
        e.ToTable("ResponseStatuses");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.ActivationId);
        e.HasIndex(x => x.ParticipantId);
    }
}
