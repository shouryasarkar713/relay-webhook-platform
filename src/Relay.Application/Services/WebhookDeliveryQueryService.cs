using Microsoft.EntityFrameworkCore;
using Relay.Application.Abstractions;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;
using Relay.Domain.Enums;

namespace Relay.Application.Services;

public sealed class WebhookDeliveryQueryService : IWebhookDeliveryQueryService
{
    private readonly IRelayDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly IWebhookDeliveryStore _deliveryStore;
    private readonly TimeProvider _timeProvider;

    public WebhookDeliveryQueryService(
        IRelayDbContext dbContext,
        ITenantContext tenantContext,
        IWebhookDeliveryStore deliveryStore,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _deliveryStore = deliveryStore;
        _timeProvider = timeProvider;
    }

    public async Task<PagedResult<WebhookDeliveryDto>> GetDeliveriesAsync(
        string? status = null,
        Guid? endpointId = null,
        string? eventType = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var query = _dbContext.WebhookDeliveries
            .Include(d => d.Endpoint)
            .Include(d => d.Event)
            .Where(d => d.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<WebhookDeliveryStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(d => d.Status == parsedStatus);
        }

        if (endpointId.HasValue)
        {
            query = query.Where(d => d.EndpointId == endpointId.Value);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(d => d.Event != null && d.Event.EventType == eventType.Trim());
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(d => d.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .Select(d => new WebhookDeliveryDto(
                d.Id,
                d.EventId,
                d.EndpointId,
                d.Endpoint != null ? d.Endpoint.Name : null,
                d.Endpoint != null ? d.Endpoint.Url : null,
                d.Event != null ? d.Event.EventType : null,
                d.Status.ToString(),
                d.CreatedAtUtc,
                d.LastAttemptAtUtc,
                d.NextAttemptAtUtc,
                d.DeliveredAtUtc,
                d.DeadLetteredAtUtc,
                d.ClaimCount,
                d.AttemptCount,
                d.RetryCycleAttemptCount,
                d.ReplayCount,
                d.LastHttpStatusCode,
                d.LastErrorCategory,
                d.LastError,
                null))
            .ToListAsync(cancellationToken);

        return new PagedResult<WebhookDeliveryDto>(items, totalCount, pageSize);
    }

    public async Task<WebhookDeliveryDto?> GetDeliveryByIdAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;

        var delivery = await _dbContext.WebhookDeliveries
            .Include(d => d.Endpoint)
            .Include(d => d.Event)
            .Include(d => d.Attempts.OrderBy(a => a.AttemptNumber))
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.TenantId == tenantId, cancellationToken);

        if (delivery is null)
        {
            return null;
        }

        var attempts = delivery.Attempts.Select(a => new WebhookDeliveryAttemptDto(
            a.Id,
            a.AttemptNumber,
            a.StartedAtUtc,
            a.CompletedAtUtc,
            a.DurationMilliseconds,
            a.HttpStatusCode,
            a.Outcome.ToString(),
            a.ErrorCategory,
            a.ResponseExcerpt)).ToList();

        return new WebhookDeliveryDto(
            delivery.Id,
            delivery.EventId,
            delivery.EndpointId,
            delivery.Endpoint?.Name,
            delivery.Endpoint?.Url,
            delivery.Event?.EventType,
            delivery.Status.ToString(),
            delivery.CreatedAtUtc,
            delivery.LastAttemptAtUtc,
            delivery.NextAttemptAtUtc,
            delivery.DeliveredAtUtc,
            delivery.DeadLetteredAtUtc,
            delivery.ClaimCount,
            delivery.AttemptCount,
            delivery.RetryCycleAttemptCount,
            delivery.ReplayCount,
            delivery.LastHttpStatusCode,
            delivery.LastErrorCategory,
            delivery.LastError,
            attempts);
    }

    public async Task<ReplayDeliveryResponse> ReplayDeliveryAsync(
        Guid deliveryId,
        string? replayKey,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        var tenantId = _tenantContext.TenantId;
        var requestedBy = $"ApiKey:{_tenantContext.ApiKeyId:D}";
        var now = _timeProvider.GetUtcNow();

        var audit = await _deliveryStore.ReplayDeliveryAsync(
            deliveryId,
            tenantId,
            replayKey,
            requestedBy,
            now,
            cancellationToken);

        // Fetch replayed count
        var delivery = await _dbContext.WebhookDeliveries
            .AsNoTracking()
            .FirstAsync(d => d.Id == deliveryId, cancellationToken);

        return new ReplayDeliveryResponse(
            audit.Id,
            deliveryId,
            delivery.Status.ToString(),
            audit.ReplayedAtUtc,
            delivery.ReplayCount,
            $"Webhook delivery {deliveryId} replayed successfully. Scheduled for immediate dispatch.");
    }

    private void EnsureAuthenticated()
    {
        if (!_tenantContext.IsAuthenticated)
        {
            throw new UnauthorizedTenantException();
        }
    }
}
