using Relay.Application.DTOs;

namespace Relay.Application.Services;

public interface IWebhookEndpointService
{
    Task<WebhookEndpointResponse> CreateEndpointAsync(
        CreateWebhookEndpointRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WebhookEndpointResponse>> GetEndpointsAsync(
        CancellationToken cancellationToken = default);

    Task<WebhookEndpointResponse?> GetEndpointByIdAsync(
        Guid endpointId,
        CancellationToken cancellationToken = default);

    Task<WebhookEndpointResponse> UpdateEndpointAsync(
        Guid endpointId,
        UpdateWebhookEndpointRequest request,
        CancellationToken cancellationToken = default);

    Task DeactivateEndpointAsync(
        Guid endpointId,
        CancellationToken cancellationToken = default);
}

public interface IWebhookDeliveryQueryService
{
    Task<PagedResult<WebhookDeliveryDto>> GetDeliveriesAsync(
        string? status = null,
        Guid? endpointId = null,
        string? eventType = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<WebhookDeliveryDto?> GetDeliveryByIdAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default);

    Task<ReplayDeliveryResponse> ReplayDeliveryAsync(
        Guid deliveryId,
        string? replayKey,
        CancellationToken cancellationToken = default);
}
