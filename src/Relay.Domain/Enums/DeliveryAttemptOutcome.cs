namespace Relay.Domain.Enums;

public enum DeliveryAttemptOutcome
{
    Started = 1,
    Succeeded = 2,
    HttpFailure = 3,
    Timeout = 4,
    NetworkFailure = 5,
    WorkerInterrupted = 6,
    UnknownOutcome = 7,
    RetryableHttpFailure = 8,
    PermanentHttpFailure = 9
}
