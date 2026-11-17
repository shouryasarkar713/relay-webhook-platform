namespace Relay.Application.Exceptions;

public class RelayApplicationException : Exception
{
    public RelayApplicationException(string message) : base(message) { }
    public RelayApplicationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ValidationException : RelayApplicationException
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string field, string error)
        : base($"Validation failed for {field}: {error}")
    {
        Errors = new Dictionary<string, string[]>
        {
            [field] = [error]
        };
    }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation failures occurred.")
    {
        Errors = errors;
    }
}

public sealed class IdempotencyConflictException : RelayApplicationException
{
    public string IdempotencyKey { get; }
    public Guid ExistingEventId { get; }

    public IdempotencyConflictException(string idempotencyKey, Guid existingEventId)
        : base($"The idempotency key '{idempotencyKey}' was already used with different payload content for event '{existingEventId}'.")
    {
        IdempotencyKey = idempotencyKey;
        ExistingEventId = existingEventId;
    }
}

public sealed class PayloadTooLargeException : RelayApplicationException
{
    public int ActualBytes { get; }
    public int MaxAllowedBytes { get; }

    public PayloadTooLargeException(int actualBytes, int maxAllowedBytes)
        : base($"Payload size {actualBytes} bytes exceeds the maximum allowed inline size of {maxAllowedBytes} bytes.")
    {
        ActualBytes = actualBytes;
        MaxAllowedBytes = maxAllowedBytes;
    }
}

public sealed class ResourceNotFoundException : RelayApplicationException
{
    public string ResourceName { get; }
    public object Key { get; }

    public ResourceNotFoundException(string resourceName, object key)
        : base($"{resourceName} with key '{key}' was not found.")
    {
        ResourceName = resourceName;
        Key = key;
    }
}

public sealed class UnauthorizedTenantException : RelayApplicationException
{
    public UnauthorizedTenantException(string message = "Tenant is not authenticated or authorized.")
        : base(message) { }
}
