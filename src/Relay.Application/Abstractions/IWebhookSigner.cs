namespace Relay.Application.Abstractions;

public sealed record WebhookSignature(long Timestamp, string SignatureHex, string HeaderValue);

public interface IWebhookSigner
{
    WebhookSignature Sign(string signingSecret, byte[] bodyBytes, long? fixedTimestamp = null);
    bool Verify(string signingSecret, byte[] bodyBytes, string signatureHeader, TimeSpan tolerance, long? currentTimestamp = null);
}
