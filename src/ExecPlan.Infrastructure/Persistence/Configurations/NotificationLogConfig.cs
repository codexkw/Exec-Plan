using ExecPlan.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class NotificationLogConfig : IEntityTypeConfiguration<NotificationLog>
{
    public void Configure(EntityTypeBuilder<NotificationLog> e)
    {
        e.ToTable("NotificationLogs");
        e.HasKey(x => x.Id);
        e.Property(x => x.Body).IsRequired().HasMaxLength(2000);
        e.HasIndex(x => x.ActivationId);
        e.HasIndex(x => x.RecipientUserId);
    }
}
