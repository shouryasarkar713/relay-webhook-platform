using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relay.Application.Abstractions;
using Relay.Application.Common;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;
using Relay.Application.Validators;
using Relay.Domain.Entities;
using Relay.Domain.Enums;

namespace Relay.Application.Services;

public sealed class EventIngestionService : IEventIngestionService
{
    private readonly IRelayDbContext _dbContext;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventIngestionService> _logger;

    public EventIngestionService(
        IRelayDbContext dbContext,
        ITenantContext tenantContext,
        TimeProvider timeProvider,
        ILogger<EventIngestionService> logger)
    {
        _dbContext = dbContext;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IngestEventResult> IngestAsync(
        IngestEventRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!_tenantContext.IsAuthenticated)
        {
            throw new UnauthorizedTenantException();
        }

        var tenantId = _tenantContext.TenantId;

        // 1. Validate request structure and idempotency key
        IngestEventValidator.Validate(request, idempotencyKey);
        var cleanIdempotencyKey = idempotencyKey.Trim();

        // 2. Compute payload fingerprint and check size limit (32 KB)
        var (minifiedJson, payloadHash, byteCount) = PayloadFingerprint.Compute(request.Payload);
        if (byteCount > PayloadFingerprint.MaxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException(byteCount, PayloadFingerprint.MaxInlinePayloadBytes);
        }

        // 3. Application pre-check for existing idempotency key
        var existingEvent = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.IdempotencyKey == cleanIdempotencyKey,
                cancellationToken);

        if (existingEvent is not null)
        {
            return HandleExistingEvent(existingEvent, payloadHash, cleanIdempotencyKey);
        }

        // 4. Atomic Event + OutboxMessage insertion inside a transaction
        var now = _timeProvider.GetUtcNow();
        var eventId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();

        var newEvent = new Event(
            id: eventId,
            tenantId: tenantId,
            eventType: request.EventType!.Trim(),
            payloadJson: minifiedJson,
            payloadHash: payloadHash,
            idempotencyKey: cleanIdempotencyKey,
            createdAtUtc: now,
            status: EventStatus.Accepted);

        var outboxMessage = new OutboxMessage(
            id: outboxId,
            eventId: eventId,
            tenantId: tenantId,
            messageType: request.EventType!.Trim(),
            payloadJson: minifiedJson,
            createdAtUtc: now,
            status: OutboxStatus.Pending);

        await using var transaction = await _dbContext.BeginTransactionAsync(cancellationToken);

        try
        {
            _dbContext.Events.Add(newEvent);
            _dbContext.OutboxMessages.Add(outboxMessage);

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Ingested event {EventId} for tenant {TenantId} with idempotency key {IdempotencyKey}",
                eventId,
                tenantId,
                cleanIdempotencyKey);

            return new IngestEventResult(
                new IngestEventResponse(newEvent.Id, newEvent.Status.ToString(), newEvent.CreatedAtUtc),
                IsReplay: false);
        }
        catch (DbUpdateException dbEx)
        {
            _logger.LogWarning(
                dbEx,
                "Concurrent submission or database conflict detected for tenant {TenantId} and key {IdempotencyKey}",
                tenantId,
                cleanIdempotencyKey);

            await transaction.RollbackAsync(cancellationToken);

            // Detach pending entries to clear EF change tracker state
            DetachEntity(newEvent);
            DetachEntity(outboxMessage);

            // Resolve existing event committed by concurrent winner
            var concurrentEvent = await _dbContext.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.TenantId == tenantId && e.IdempotencyKey == cleanIdempotencyKey,
                    cancellationToken);

            if (concurrentEvent is not null)
            {
                return HandleExistingEvent(concurrentEvent, payloadHash, cleanIdempotencyKey);
            }

            throw;
        }
    }

    private IngestEventResult HandleExistingEvent(Event existing, string computedHash, string idempotencyKey)
    {
        if (string.Equals(existing.PayloadHash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Idempotency hit: Replaying original event {EventId} for tenant {TenantId} with key {IdempotencyKey}",
                existing.Id,
                existing.TenantId,
                idempotencyKey);

            return new IngestEventResult(
                new IngestEventResponse(existing.Id, existing.Status.ToString(), existing.CreatedAtUtc),
                IsReplay: true);
        }

        _logger.LogWarning(
            "Idempotency conflict: Key {IdempotencyKey} was already used with hash {ExistingHash}, incoming hash was {NewHash}",
            idempotencyKey,
            existing.PayloadHash,
            computedHash);

        throw new IdempotencyConflictException(idempotencyKey, existing.Id);
    }

    private void DetachEntity<T>(T entity) where T : class
    {
        if (_dbContext is DbContext concreteContext)
        {
            concreteContext.Entry(entity).State = EntityState.Detached;
        }
    }
}
