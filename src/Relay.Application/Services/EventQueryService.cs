using Microsoft.EntityFrameworkCore;
using Relay.Application.Abstractions;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;

namespace Relay.Application.Services;

public sealed class EventQueryService : IEventQueryService
{
    private const int MaxTake = 100;
    private const int DefaultTake = 20;

    private readonly IRelayDbContext _dbContext;
    private readonly ITenantContext _tenantContext;

    public EventQueryService(IRelayDbContext dbContext, ITenantContext tenantContext)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
    }

    public async Task<EventDto?> GetEventByIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        if (!_tenantContext.IsAuthenticated)
        {
            throw new UnauthorizedTenantException();
        }

        var tenantId = _tenantContext.TenantId;

        // Query strictly scoped to authenticated tenant
        var evt = await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId && e.TenantId == tenantId)
            .Select(e => new EventDto(
                e.Id,
                e.TenantId,
                e.EventType,
                e.PayloadJson,
                e.PayloadHash,
                e.IdempotencyKey,
                e.CreatedAtUtc,
                e.Status.ToString()))
            .FirstOrDefaultAsync(cancellationToken);

        return evt;
    }

    public async Task<PagedResult<EventDto>> ListEventsAsync(int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        if (!_tenantContext.IsAuthenticated)
        {
            throw new UnauthorizedTenantException();
        }

        var tenantId = _tenantContext.TenantId;

        var effectiveTake = take switch
        {
            <= 0 => DefaultTake,
            > MaxTake => MaxTake,
            _ => take
        };

        var items = await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(effectiveTake)
            .Select(e => new EventDto(
                e.Id,
                e.TenantId,
                e.EventType,
                e.PayloadJson,
                e.PayloadHash,
                e.IdempotencyKey,
                e.CreatedAtUtc,
                e.Status.ToString()))
            .ToListAsync(cancellationToken);

        return new PagedResult<EventDto>(items, items.Count, effectiveTake);
    }
}
