using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Relay.Infrastructure;

namespace Relay.Api.Controllers;

[ApiController]
[Route("health")]
[AllowAnonymous]
public sealed class HealthController : ControllerBase
{
    private readonly RelayDbContext _dbContext;

    public HealthController(RelayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("live")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Live()
    {
        return Ok(new
        {
            status = "Live",
            timestampUtc = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        try
        {
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                return Ok(new
                {
                    status = "Ready",
                    database = "Connected",
                    timestampUtc = DateTimeOffset.UtcNow
                });
            }

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "NotReady",
                database = "CannotConnect",
                timestampUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "NotReady",
                database = "Unhealthy",
                error = ex.Message,
                timestampUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
