using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;
using Relay.Domain.Enums;

namespace Relay.Infrastructure.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.EventId)
            .IsRequired();

        builder.Property(o => o.TenantId)
            .IsRequired();

        builder.Property(o => o.MessageType)
            .IsRequired()
            .HasMaxLength(250);

        builder.Property(o => o.PayloadJson)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(o => o.CreatedAtUtc)
            .IsRequired();

        builder.Property(o => o.PublishedAtUtc)
            .IsRequired(false);

        builder.Property(o => o.AttemptCount)
            .IsRequired();

        builder.Property(o => o.LastError)
            .IsRequired(false)
            .HasColumnType("nvarchar(max)");

        builder.Property(o => o.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasOne(o => o.Event)
            .WithOne(e => e.OutboxMessage)
            .HasForeignKey<OutboxMessage>(o => o.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        // Core Requirement 3: Compound index for outbox polling
        builder.HasIndex(o => new { o.Status, o.CreatedAtUtc });

        builder.HasIndex(o => o.TenantId);
    }
}
