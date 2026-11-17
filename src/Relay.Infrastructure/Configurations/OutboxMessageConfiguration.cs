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

        builder.Property(o => o.ClaimCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(o => o.PublishAttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(o => o.LastError)
            .IsRequired(false)
            .HasColumnType("nvarchar(max)");

        builder.Property(o => o.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(o => o.LeaseToken)
            .IsRequired(false);

        builder.Property(o => o.LeaseExpiresAtUtc)
            .IsRequired(false);

        builder.Property(o => o.NextAttemptAtUtc)
            .IsRequired();

        builder.Property(o => o.LastAttemptAtUtc)
            .IsRequired(false);

        builder.HasOne(o => o.Event)
            .WithOne(e => e.OutboxMessage)
            .HasForeignKey<OutboxMessage>(o => o.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indices for outbox polling, lease expiration, and dispatch queries
        builder.HasIndex(o => new { o.Status, o.NextAttemptAtUtc });
        builder.HasIndex(o => new { o.Status, o.LeaseExpiresAtUtc });
        builder.HasIndex(o => o.LeaseToken);
        builder.HasIndex(o => new { o.Status, o.CreatedAtUtc });

        builder.HasIndex(o => o.TenantId);
    }
}
