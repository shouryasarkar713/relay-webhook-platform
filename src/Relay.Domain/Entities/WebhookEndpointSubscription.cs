namespace Relay.Domain.Entities;

public sealed class WebhookEndpointSubscription
{
    private WebhookEndpointSubscription() { }

    public WebhookEndpointSubscription(Guid id, Guid endpointId, string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            throw new ArgumentException("EventType cannot be empty.", nameof(eventType));
        }

        Id = id;
        EndpointId = endpointId;
        EventType = eventType.Trim();
    }

    public Guid Id { get; private set; }
    public Guid EndpointId { get; private set; }
    public string EventType { get; private set; } = string.Empty;

    public WebhookEndpoint? Endpoint { get; private set; }
}
