using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;

namespace Relay.Infrastructure.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("ApiKeys");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.TenantId)
            .IsRequired();

        builder.Property(k => k.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(k => k.KeyPrefix)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(k => k.KeyHash)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(k => k.CreatedAtUtc)
            .IsRequired();

        builder.Property(k => k.LastUsedAtUtc)
            .IsRequired(false);

        builder.Property(k => k.ExpiresAtUtc)
            .IsRequired(false);

        builder.Property(k => k.RevokedAtUtc)
            .IsRequired(false);

        // Fast O(1) prefix lookup index
        builder.HasIndex(k => k.KeyPrefix)
            .IsUnique();

        builder.HasIndex(k => k.TenantId);
    }
}
