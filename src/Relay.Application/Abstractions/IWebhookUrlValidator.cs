namespace Relay.Application.Abstractions;

public sealed record UrlValidationResult(bool IsValid, string? ErrorMessage = null)
{
    public static UrlValidationResult Success() => new(true);
    public static UrlValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

public interface IWebhookUrlValidator
{
    Task<UrlValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken = default);
}
