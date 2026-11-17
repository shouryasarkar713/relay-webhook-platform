using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Relay.Application.Abstractions;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;
using Relay.Domain.Entities;

namespace Relay.Application.Services;

public sealed class WebhookEndpointService : IWebhookEndpointService
{
    private readonly IRelayDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IEndpointSecretProtector _secretProtector;
    private readonly IWebhookUrlValidator _urlValidator;
    private readonly TimeProvider _timeProvider;

    public WebhookEndpointService(
        IRelayDbContext dbContext,
        ITenantContext tenantContext,
        IEndpointSecretProtector secretProtector,
        IWebhookUrlValidator urlValidator,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _secretProtector = secretProtector;
        _urlValidator = urlValidator;
        _timeProvider = timeProvider;
    }

    public async Task<WebhookEndpointResponse> CreateEndpointAsync(
        CreateWebhookEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException(nameof(request.Name), "Endpoint name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            throw new ValidationException(nameof(request.Url), "A valid absolute URL is required.");
        }

        var validationResult = await _urlValidator.ValidateAsync(uri, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(nameof(request.Url), validationResult.ErrorMessage ?? "URL failed security validation.");
        }

        if (request.EventTypes == null || request.EventTypes.Count == 0 || request.EventTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ValidationException(nameof(request.EventTypes), "At least one valid event type subscription is required.");
        }

        var now = _timeProvider.GetUtcNow();
        var rawSecret = $"whsec_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var protectedSecret = _secretProtector.Protect(rawSecret);

        var endpoint = new WebhookEndpoint(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            name: request.Name,
            url: request.Url,
            signingSecretProtected: protectedSecret,
            createdAtUtc: now);

        endpoint.SetSubscriptions(request.EventTypes);

        _dbContext.WebhookEndpoints.Add(endpoint);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Return raw signing secret EXACTLY ONCE upon creation
        return new WebhookEndpointResponse(
            endpoint.Id,
            endpoint.Name,
            endpoint.Url,
            endpoint.Subscriptions.Select(s => s.EventType).ToList(),
            endpoint.IsActive,
            endpoint.CreatedAtUtc,
            endpoint.UpdatedAtUtc,
            endpoint.DisabledAtUtc,
            endpoint.LastDeliveryAtUtc,
            SigningSecret: rawSecret,
            CircuitState: endpoint.CircuitState.ToString(),
            ConsecutiveFailureCount: endpoint.ConsecutiveFailureCount);
    }

    public async Task<IReadOnlyList<WebhookEndpointResponse>> GetEndpointsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        var endpoints = await _dbContext.WebhookEndpoints
            .Include(e => e.Subscriptions)
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // NEVER return signing secret in list queries
        return endpoints.Select(e => new WebhookEndpointResponse(
            e.Id,
            e.Name,
            e.Url,
            e.Subscriptions.Select(s => s.EventType).ToList(),
            e.IsActive,
            e.CreatedAtUtc,
            e.UpdatedAtUtc,
            e.DisabledAtUtc,
            e.LastDeliveryAtUtc,
            SigningSecret: null,
            CircuitState: e.CircuitState.ToString(),
            ConsecutiveFailureCount: e.ConsecutiveFailureCount)).ToList();
    }

    public async Task<WebhookEndpointResponse?> GetEndpointByIdAsync(
        Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        var endpoint = await _dbContext.WebhookEndpoints
            .Include(e => e.Subscriptions)
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.TenantId == tenantId, cancellationToken);

        if (endpoint is null)
        {
            return null;
        }

        // NEVER return signing secret in get-by-id queries
        return new WebhookEndpointResponse(
            endpoint.Id,
            endpoint.Name,
            endpoint.Url,
            endpoint.Subscriptions.Select(s => s.EventType).ToList(),
            endpoint.IsActive,
            endpoint.CreatedAtUtc,
            endpoint.UpdatedAtUtc,
            endpoint.DisabledAtUtc,
            endpoint.LastDeliveryAtUtc,
            SigningSecret: null,
            CircuitState: endpoint.CircuitState.ToString(),
            ConsecutiveFailureCount: endpoint.ConsecutiveFailureCount);
    }

    public async Task<WebhookEndpointResponse> UpdateEndpointAsync(
        Guid endpointId,
        UpdateWebhookEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        var endpoint = await _dbContext.WebhookEndpoints
            .Include(e => e.Subscriptions)
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.TenantId == tenantId, cancellationToken);

        if (endpoint is null)
        {
            throw new ResourceNotFoundException("WebhookEndpoint", endpointId);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException(nameof(request.Name), "Endpoint name cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            throw new ValidationException(nameof(request.Url), "A valid absolute URL is required.");
        }

        var validationResult = await _urlValidator.ValidateAsync(uri, cancellationToken);
        if (!validationResult.IsValid)
        {
            throw new ValidationException(nameof(request.Url), validationResult.ErrorMessage ?? "URL failed security validation.");
        }

        if (request.EventTypes == null || request.EventTypes.Count == 0 || request.EventTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ValidationException(nameof(request.EventTypes), "At least one valid event type subscription is required.");
        }

        var now = _timeProvider.GetUtcNow();
        endpoint.Update(request.Name, request.Url, now);
        endpoint.SetSubscriptions(request.EventTypes);

        if (request.IsActive.HasValue)
        {
            if (request.IsActive.Value && !endpoint.IsActive)
            {
                // Re-activating endpoint resets circuit and clears disabled timestamp
                endpoint.ResetCircuit(now);
            }
            else if (!request.IsActive.Value && endpoint.IsActive)
            {
                endpoint.Deactivate(now);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new WebhookEndpointResponse(
            endpoint.Id,
            endpoint.Name,
            endpoint.Url,
            endpoint.Subscriptions.Select(s => s.EventType).ToList(),
            endpoint.IsActive,
            endpoint.CreatedAtUtc,
            endpoint.UpdatedAtUtc,
            endpoint.DisabledAtUtc,
            endpoint.LastDeliveryAtUtc,
            SigningSecret: null,
            CircuitState: endpoint.CircuitState.ToString(),
            ConsecutiveFailureCount: endpoint.ConsecutiveFailureCount);
    }

    public async Task DeactivateEndpointAsync(
        Guid endpointId,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        var endpoint = await _dbContext.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.TenantId == tenantId, cancellationToken);

        if (endpoint is null)
        {
            throw new ResourceNotFoundException("WebhookEndpoint", endpointId);
        }

        endpoint.Deactivate(_timeProvider.GetUtcNow());
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private void EnsureAuthenticated()
    {
        if (!_tenantContext.IsAuthenticated)
        {
            throw new UnauthorizedTenantException();
        }
    }
}
