using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;

namespace Relay.Infrastructure.Configurations;

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(t => t.Slug)
            .IsRequired()
            .HasMaxLength(50);

        builder.HasIndex(t => t.Slug)
            .IsUnique();

        builder.Property(t => t.CreatedAtUtc)
            .IsRequired();

        builder.Property(t => t.IsActive)
            .IsRequired();

        builder.HasMany(t => t.ApiKeys)
            .WithOne(k => k.Tenant)
            .HasForeignKey(k => k.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Events)
            .WithOne(e => e.Tenant)
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
