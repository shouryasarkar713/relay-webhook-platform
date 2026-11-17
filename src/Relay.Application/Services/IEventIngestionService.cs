using Relay.Application.DTOs;

namespace Relay.Application.Services;

public readonly record struct IngestEventResult(
    IngestEventResponse Response,
    bool IsReplay);

public interface IEventIngestionService
{
    Task<IngestEventResult> IngestAsync(
        IngestEventRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
