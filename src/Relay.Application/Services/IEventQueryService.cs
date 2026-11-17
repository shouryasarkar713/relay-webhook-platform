using Relay.Application.DTOs;

namespace Relay.Application.Services;

public interface IEventQueryService
{
    Task<EventDto?> GetEventByIdAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task<PagedResult<EventDto>> ListEventsAsync(int take = 20, CancellationToken cancellationToken = default);
}
