using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;
using Relay.Domain.Enums;

namespace Relay.Infrastructure.Configurations;

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("Events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId)
            .IsRequired();

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(e => e.PayloadJson)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(e => e.PayloadHash)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(e => e.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        builder.Property(e => e.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // Core Requirement 1: Database-enforced idempotency per tenant
        builder.HasIndex(e => new { e.TenantId, e.IdempotencyKey })
            .IsUnique();

        // Core Requirement 2: Efficient tenant event history ordering
        builder.HasIndex(e => new { e.TenantId, e.CreatedAtUtc });
    }
}
