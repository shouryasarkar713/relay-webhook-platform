using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;

namespace Relay.Infrastructure.Configurations;

public sealed class WebhookEventFanoutConfiguration : IEntityTypeConfiguration<WebhookEventFanout>
{
    public void Configure(EntityTypeBuilder<WebhookEventFanout> builder)
    {
        builder.ToTable("WebhookEventFanouts");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.EventId)
            .IsRequired();

        builder.Property(f => f.TenantId)
            .IsRequired();

        builder.Property(f => f.ProcessedAtUtc)
            .IsRequired();

        builder.Property(f => f.MatchedEndpointCount)
            .IsRequired();

        builder.HasIndex(f => f.EventId)
            .IsUnique();

        builder.HasIndex(f => new { f.TenantId, f.ProcessedAtUtc });
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("WebhookDeliveries");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.EventId)
            .IsRequired();

        builder.Property(d => d.EndpointId)
            .IsRequired();

        builder.Property(d => d.TenantId)
            .IsRequired();

        builder.Property(d => d.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(d => d.CreatedAtUtc)
            .IsRequired();

        builder.Property(d => d.LastAttemptAtUtc)
            .IsRequired(false);

        builder.Property(d => d.NextAttemptAtUtc)
            .IsRequired();

        builder.Property(d => d.DeliveredAtUtc)
            .IsRequired(false);

        builder.Property(d => d.DeadLetteredAtUtc)
            .IsRequired(false);

        builder.Property(d => d.ClaimCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.AttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.RetryCycleAttemptCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.RetryCycleStartedAtUtc)
            .IsRequired();

        builder.Property(d => d.ReplayCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(d => d.LastHttpStatusCode)
            .IsRequired(false);

        builder.Property(d => d.LastErrorCategory)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(d => d.LastError)
            .IsRequired(false)
            .HasColumnType("nvarchar(max)");

        builder.Property(d => d.LeaseToken)
            .IsRequired(false);

        builder.Property(d => d.LeaseExpiresAtUtc)
            .IsRequired(false);

        builder.HasOne(d => d.Event)
            .WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Endpoint)
            .WithMany()
            .HasForeignKey(d => d.EndpointId)
            .OnDelete(DeleteBehavior.Restrict);

        // Core Requirement: Unique constraint preventing duplicate delivery records per event/endpoint
        builder.HasIndex(d => new { d.EventId, d.EndpointId })
            .IsUnique();

        // Indexes for claiming & leasing
        builder.HasIndex(d => new { d.Status, d.NextAttemptAtUtc });
        builder.HasIndex(d => new { d.Status, d.CreatedAtUtc });
        builder.HasIndex(d => new { d.Status, d.LeaseExpiresAtUtc });
        builder.HasIndex(d => d.LeaseToken);

        // Indexes for history filtering
        builder.HasIndex(d => new { d.TenantId, d.CreatedAtUtc });
        builder.HasIndex(d => new { d.EndpointId, d.CreatedAtUtc });
        builder.HasIndex(d => new { d.TenantId, d.Status });
    }
}

public sealed class WebhookDeliveryAttemptConfiguration : IEntityTypeConfiguration<WebhookDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryAttempt> builder)
    {
        builder.ToTable("WebhookDeliveryAttempts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.DeliveryId)
            .IsRequired();

        builder.Property(a => a.AttemptNumber)
            .IsRequired();

        builder.Property(a => a.StartedAtUtc)
            .IsRequired();

        builder.Property(a => a.CompletedAtUtc)
            .IsRequired();

        builder.Property(a => a.DurationMilliseconds)
            .IsRequired();

        builder.Property(a => a.HttpStatusCode)
            .IsRequired(false);

        builder.Property(a => a.Outcome)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(a => a.ErrorCategory)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(a => a.ResponseExcerpt)
            .IsRequired(false)
            .HasMaxLength(4000);

        builder.HasOne(a => a.Delivery)
            .WithMany(d => d.Attempts)
            .HasForeignKey(a => a.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.DeliveryId, a.AttemptNumber })
            .IsUnique();
    }
}

public sealed class WebhookDeliveryReplayAuditConfiguration : IEntityTypeConfiguration<WebhookDeliveryReplayAudit>
{
    public void Configure(EntityTypeBuilder<WebhookDeliveryReplayAudit> builder)
    {
        builder.ToTable("WebhookDeliveryReplayAudits");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.DeliveryId)
            .IsRequired();

        builder.Property(r => r.ReplayKey)
            .IsRequired(false)
            .HasMaxLength(100);

        builder.Property(r => r.RequestedBy)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.PreviousStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(r => r.ReplayedAtUtc)
            .IsRequired();

        builder.HasOne(r => r.Delivery)
            .WithMany()
            .HasForeignKey(r => r.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.TenantId, r.ReplayedAtUtc });
        builder.HasIndex(r => new { r.TenantId, r.DeliveryId, r.ReplayKey })
            .IsUnique()
            .HasFilter("[ReplayKey] IS NOT NULL");
    }
}
