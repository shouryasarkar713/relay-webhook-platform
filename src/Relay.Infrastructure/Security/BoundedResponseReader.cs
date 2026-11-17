using System.Text;

namespace Relay.Infrastructure.Security;

public static class BoundedResponseReader
{
    public static async Task<string?> ReadExcerptAsync(
        HttpContent? content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content is null || maxBytes <= 0)
        {
            return null;
        }

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[maxBytes];
            var totalBytesRead = 0;

            while (totalBytesRead < maxBytes)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(totalBytesRead, maxBytes - totalBytesRead),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
            }

            if (totalBytesRead == 0)
            {
                return string.Empty;
            }

            // Decode UTF-8 safely without throwing if the stream was cut mid-character
            var decoder = Encoding.UTF8.GetDecoder();
            var charBuffer = new char[totalBytesRead];
            var charsUsed = decoder.GetChars(buffer, 0, totalBytesRead, charBuffer, 0, flush: false);
            return new string(charBuffer, 0, charsUsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"[Response read error: {ex.Message}]";
        }
    }
}
