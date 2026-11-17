using System.Text.Json;
using System.Text.RegularExpressions;
using Relay.Application.Common;
using Relay.Application.DTOs;
using Relay.Application.Exceptions;

namespace Relay.Application.Validators;

public static partial class IngestEventValidator
{
    private const int MaxEventTypeLength = 250;
    private const int MaxIdempotencyKeyLength = 256;

    [GeneratedRegex(@"^[a-zA-Z0-9_\.\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidEventTypeRegex();

    public static void Validate(IngestEventRequest request, string? idempotencyKey)
    {
        var errors = new Dictionary<string, List<string>>();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            AddError(errors, "Idempotency-Key", "Idempotency-Key header is required.");
        }
        else if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            AddError(errors, "Idempotency-Key", $"Idempotency-Key exceeds maximum length of {MaxIdempotencyKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            AddError(errors, nameof(request.EventType), "EventType is required.");
        }
        else
        {
            var trimmedType = request.EventType.Trim();
            if (trimmedType.Length > MaxEventTypeLength)
            {
                AddError(errors, nameof(request.EventType), $"EventType exceeds maximum length of {MaxEventTypeLength} characters.");
            }
            else if (!ValidEventTypeRegex().IsMatch(trimmedType))
            {
                AddError(errors, nameof(request.EventType), "EventType can only contain alphanumeric characters, dots, underscores, and hyphens.");
            }
        }

        if (request.Payload.ValueKind == JsonValueKind.Undefined || request.Payload.ValueKind == JsonValueKind.Null)
        {
            AddError(errors, nameof(request.Payload), "Payload cannot be null or undefined.");
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors.ToDictionary(k => k.Key, v => v.Value.ToArray()));
        }
    }

    private static void AddError(Dictionary<string, List<string>> dict, string key, string message)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = [];
            dict[key] = list;
        }
        list.Add(message);
    }
}
