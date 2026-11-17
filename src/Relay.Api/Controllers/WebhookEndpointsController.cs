using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relay.Application.DTOs;
using Relay.Application.Services;

namespace Relay.Api.Controllers;

[ApiController]
[Route("v1/endpoints")]
[Authorize]
public sealed class WebhookEndpointsController : ControllerBase
{
    private readonly IWebhookEndpointService _endpointService;

    public WebhookEndpointsController(IWebhookEndpointService endpointService)
    {
        _endpointService = endpointService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(WebhookEndpointResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateEndpoint(
        [FromBody] CreateWebhookEndpointRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _endpointService.CreateEndpointAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetEndpointById), new { endpointId = response.Id }, response);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookEndpointResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetEndpoints(CancellationToken cancellationToken)
    {
        var endpoints = await _endpointService.GetEndpointsAsync(cancellationToken);
        return Ok(endpoints);
    }

    [HttpGet("{endpointId:guid}")]
    [ProducesResponseType(typeof(WebhookEndpointResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEndpointById(
        [FromRoute] Guid endpointId,
        CancellationToken cancellationToken)
    {
        var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId, cancellationToken);
        if (endpoint is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Endpoint Not Found",
                Status = StatusCodes.Status404NotFound,
                Detail = $"Webhook endpoint '{endpointId}' was not found for the authenticated tenant."
            });
        }

        return Ok(endpoint);
    }

    [HttpPut("{endpointId:guid}")]
    [ProducesResponseType(typeof(WebhookEndpointResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEndpoint(
        [FromRoute] Guid endpointId,
        [FromBody] UpdateWebhookEndpointRequest request,
        CancellationToken cancellationToken)
    {
        var updated = await _endpointService.UpdateEndpointAsync(endpointId, request, cancellationToken);
        return Ok(updated);
    }

    [HttpDelete("{endpointId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateEndpoint(
        [FromRoute] Guid endpointId,
        CancellationToken cancellationToken)
    {
        await _endpointService.DeactivateEndpointAsync(endpointId, cancellationToken);
        return NoContent();
    }
}
