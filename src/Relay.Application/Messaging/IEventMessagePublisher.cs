namespace Relay.Application.Messaging;

public interface IEventMessagePublisher
{
    Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken = default);
}
