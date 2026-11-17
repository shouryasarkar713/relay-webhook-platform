using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Relay.Application.Common;

public static class PayloadFingerprint
{
    public const int MaxInlinePayloadBytes = 32 * 1024; // 32 KB limit for Phase 1

    private static readonly JsonSerializerOptions MinifiedOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static (string MinifiedJson, string HashHex, int ByteCount) Compute(JsonElement element)
    {
        // Serialize element deterministically (minified, with raw UTF-8 characters preserved)
        var minified = JsonSerializer.Serialize(element, MinifiedOptions);

        var bytes = Encoding.UTF8.GetBytes(minified);
        var hashBytes = SHA256.HashData(bytes);
        var hashHex = Convert.ToHexStringLower(hashBytes);

        return (minified, hashHex, bytes.Length);
    }

    public static string ComputeStringHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
