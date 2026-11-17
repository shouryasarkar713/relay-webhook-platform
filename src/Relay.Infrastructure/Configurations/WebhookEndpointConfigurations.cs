using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Entities;

namespace Relay.Infrastructure.Configurations;

public sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpoint>
{
    public void Configure(EntityTypeBuilder<WebhookEndpoint> builder)
    {
        builder.ToTable("WebhookEndpoints");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId)
            .IsRequired();

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Url)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(e => e.SigningSecretProtected)
            .IsRequired()
            .HasColumnType("nvarchar(max)");

        builder.Property(e => e.IsActive)
            .IsRequired();

        builder.Property(e => e.CreatedAtUtc)
            .IsRequired();

        builder.Property(e => e.UpdatedAtUtc)
            .IsRequired();

        builder.Property(e => e.DisabledAtUtc)
            .IsRequired(false);

        builder.Property(e => e.LastDeliveryAtUtc)
            .IsRequired(false);

        // Circuit Breaker columns
        builder.Property(e => e.CircuitState)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(Domain.Enums.EndpointCircuitState.Closed);

        builder.Property(e => e.ConsecutiveFailureCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(e => e.CircuitOpenedAtUtc)
            .IsRequired(false);

        builder.Property(e => e.CircuitHalfOpenProbeActive)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(e => e.HalfOpenProbeToken)
            .IsRequired(false);

        builder.Property(e => e.HalfOpenProbeLeaseExpiresAtUtc)
            .IsRequired(false);

        builder.HasOne(e => e.Tenant)
            .WithMany()
            .HasForeignKey(e => e.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(e => e.Subscriptions)
            .WithOne(s => s.Endpoint)
            .HasForeignKey(s => s.EndpointId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.TenantId, e.IsActive });
    }
}

public sealed class WebhookEndpointSubscriptionConfiguration : IEntityTypeConfiguration<WebhookEndpointSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookEndpointSubscription> builder)
    {
        builder.ToTable("WebhookEndpointSubscriptions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.EndpointId)
            .IsRequired();

        builder.Property(s => s.EventType)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(s => new { s.EndpointId, s.EventType })
            .IsUnique();

        // Index to quickly fan out matching endpoints by event type
        builder.HasIndex(s => s.EventType);
    }
}
