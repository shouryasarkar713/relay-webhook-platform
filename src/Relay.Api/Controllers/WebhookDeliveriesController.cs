using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relay.Application.DTOs;
using Relay.Application.Services;

namespace Relay.Api.Controllers;

[ApiController]
[Route("v1/deliveries")]
[Authorize]
public sealed class WebhookDeliveriesController : ControllerBase
{
    private readonly IWebhookDeliveryQueryService _queryService;

    public WebhookDeliveriesController(IWebhookDeliveryQueryService queryService)
    {
        _queryService = queryService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<WebhookDeliveryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] string? status,
        [FromQuery] Guid? endpointId,
        [FromQuery] string? eventType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _queryService.GetDeliveriesAsync(status, endpointId, eventType, page, pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{deliveryId:guid}")]
    [ProducesResponseType(typeof(WebhookDeliveryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDeliveryById(
        [FromRoute] Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var delivery = await _queryService.GetDeliveryByIdAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Delivery Not Found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Webhook delivery '{deliveryId}' was not found for the authenticated tenant."
            });
        }

        return Ok(delivery);
    }

    [HttpPost("{deliveryId:guid}/replay")]
    [ProducesResponseType(typeof(ReplayDeliveryResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReplayDelivery(
        [FromRoute] Guid deliveryId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _queryService.ReplayDeliveryAsync(deliveryId, idempotencyKey, cancellationToken);
            return Accepted(response);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Replay Prohibited",
                Status = StatusCodes.Status409Conflict,
                Detail = ex.Message
            });
        }
    }
}
