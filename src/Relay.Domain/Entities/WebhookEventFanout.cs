namespace Relay.Domain.Entities;

public sealed class WebhookEventFanout
{
    private WebhookEventFanout() { }

    public WebhookEventFanout(
        Guid id,
        Guid eventId,
        Guid tenantId,
        DateTimeOffset processedAtUtc,
        int matchedEndpointCount)
    {
        Id = id;
        EventId = eventId;
        TenantId = tenantId;
        ProcessedAtUtc = processedAtUtc;
        MatchedEndpointCount = matchedEndpointCount;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid TenantId { get; private set; }
    public DateTimeOffset ProcessedAtUtc { get; private set; }
    public int MatchedEndpointCount { get; private set; }

    public Event? Event { get; private set; }
}
