using System.Net.Http.Headers;

namespace Relay.Application.Abstractions;

public sealed record ParsedRetryAfter(
    TimeSpan RequestedDelay,
    DateTimeOffset TargetTimeUtc,
    bool ExceedsMaxHorizon);

public interface IRetryAfterParser
{
    ParsedRetryAfter? Parse(HttpResponseHeaders headers, TimeSpan maxHorizon);
    ParsedRetryAfter? ParseRaw(string? headerValue, TimeSpan maxHorizon);
}
