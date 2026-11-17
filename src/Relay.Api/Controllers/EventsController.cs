using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relay.Application.DTOs;
using Relay.Application.Services;

namespace Relay.Api.Controllers;

[ApiController]
[Route("v1/events")]
[Authorize]
public sealed class EventsController : ControllerBase
{
    private readonly IEventIngestionService _ingestionService;
    private readonly IEventQueryService _queryService;
    private readonly ILogger<EventsController> _logger;

    public EventsController(
        IEventIngestionService ingestionService,
        IEventQueryService queryService,
        ILogger<EventsController> logger)
    {
        _ingestionService = ingestionService;
        _queryService = queryService;
        _logger = logger;
    }

    /// <summary>
    /// Durably ingests a new event with guaranteed idempotency and Transactional Outbox persistence.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(IngestEventResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Ingest(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] IngestEventRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing Required Header",
                Detail = "The 'Idempotency-Key' request header is required.",
                Instance = HttpContext.Request.Path
            });
        }

        var result = await _ingestionService.IngestAsync(request, idempotencyKey, cancellationToken);

        // Location header points to /v1/events/{id}
        return AcceptedAtAction(
            actionName: nameof(GetById),
            routeValues: new { eventId = result.Response.EventId },
            value: result.Response);
    }

    /// <summary>
    /// Retrieves details of an ingested event by ID. Strictly scoped to the authenticated tenant.
    /// </summary>
    [HttpGet("{eventId:guid}")]
    [ProducesResponseType(typeof(EventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById([FromRoute] Guid eventId, CancellationToken cancellationToken)
    {
        var evt = await _queryService.GetEventByIdAsync(eventId, cancellationToken);
        if (evt is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Event Not Found",
                Detail = $"Event with ID '{eventId}' was not found for the authenticated tenant.",
                Instance = HttpContext.Request.Path
            });
        }

        return Ok(evt);
    }

    /// <summary>
    /// Lists ingested events for the authenticated tenant, sorted newest first.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<EventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] int take = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.ListEventsAsync(take, cancellationToken);
        return Ok(result);
    }
}
