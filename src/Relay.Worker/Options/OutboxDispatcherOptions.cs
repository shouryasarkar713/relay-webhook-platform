namespace Relay.Worker.Options;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "OutboxDispatcher";

    public int BatchSize { get; set; } = 50;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxConcurrency { get; set; } = 10;

    public int MaxRetryAttempts { get; set; } = 5;

    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (BatchSize <= 0)
        {
            throw new InvalidOperationException($"{nameof(BatchSize)} must be greater than 0.");
        }

        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(PollingInterval)} must be greater than 0.");
        }

        if (LeaseDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(LeaseDuration)} must be greater than 0.");
        }

        if (MaxConcurrency <= 0)
        {
            throw new InvalidOperationException($"{nameof(MaxConcurrency)} must be greater than 0.");
        }

        if (MaxRetryAttempts < 0)
        {
            throw new InvalidOperationException($"{nameof(MaxRetryAttempts)} cannot be negative.");
        }

        if (BaseRetryDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(BaseRetryDelay)} must be greater than 0.");
        }

        if (MaxRetryDelay < BaseRetryDelay)
        {
            throw new InvalidOperationException($"{nameof(MaxRetryDelay)} must be greater than or equal to {nameof(BaseRetryDelay)}.");
        }
    }
}
