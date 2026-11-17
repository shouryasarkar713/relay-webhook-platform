namespace Relay.Application.Abstractions;

public interface ITenantContext
{
    Guid TenantId { get; }
    Guid ApiKeyId { get; }
    bool IsAuthenticated { get; }
}
