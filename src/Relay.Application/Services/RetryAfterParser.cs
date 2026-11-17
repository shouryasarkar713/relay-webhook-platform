using System.Globalization;
using System.Net.Http.Headers;
using Relay.Application.Abstractions;

namespace Relay.Application.Services;

public sealed class RetryAfterParser : IRetryAfterParser
{
    private readonly TimeProvider _timeProvider;

    public RetryAfterParser(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ParsedRetryAfter? Parse(HttpResponseHeaders headers, TimeSpan maxHorizon)
    {
        if (headers.RetryAfter is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        // 1. Delta-seconds
        if (headers.RetryAfter.Delta.HasValue)
        {
            var delta = headers.RetryAfter.Delta.Value;
            if (delta < TimeSpan.Zero)
            {
                return null;
            }

            var targetTime = now.Add(delta);
            var exceedsHorizon = delta > maxHorizon;
            return new ParsedRetryAfter(delta, targetTime, exceedsHorizon);
        }

        // 2. HTTP-date
        if (headers.RetryAfter.Date.HasValue)
        {
            var date = headers.RetryAfter.Date.Value.ToUniversalTime();
            var delay = date > now ? date - now : TimeSpan.Zero;
            var exceedsHorizon = delay > maxHorizon;
            return new ParsedRetryAfter(delay, date, exceedsHorizon);
        }

        return null;
    }

    public ParsedRetryAfter? ParseRaw(string? headerValue, TimeSpan maxHorizon)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var trimmed = headerValue.Trim();
        var now = _timeProvider.GetUtcNow();

        // 1. Try delta-seconds integer
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds < 0)
            {
                return null;
            }

            var delay = TimeSpan.FromSeconds(seconds);
            var targetTime = now.Add(delay);
            var exceedsHorizon = delay > maxHorizon;
            return new ParsedRetryAfter(delay, targetTime, exceedsHorizon);
        }

        // 2. Try HTTP-date (RFC 1123, RFC 850, asctime)
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
        {
            var delay = parsedDate > now ? parsedDate - now : TimeSpan.Zero;
            var exceedsHorizon = delay > maxHorizon;
            return new ParsedRetryAfter(delay, parsedDate, exceedsHorizon);
        }

        return null;
    }
}
