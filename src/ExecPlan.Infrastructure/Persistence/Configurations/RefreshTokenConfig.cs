using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ExecPlan.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfig : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> e)
    {
        e.ToTable("RefreshTokens");
        e.HasKey(x => x.Id);
        e.Property(x => x.TokenHash).IsRequired().HasMaxLength(200);
        e.HasIndex(x => x.TokenHash).IsUnique();
        e.Property(x => x.ReplacedByTokenHash).HasMaxLength(200);
        e.HasIndex(x => x.UserId);
    }
}
