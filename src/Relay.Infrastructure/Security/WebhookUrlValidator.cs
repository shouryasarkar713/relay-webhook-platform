using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;

namespace Relay.Infrastructure.Security;

public sealed class WebhookSecurityOptions
{
    public const string SectionName = "WebhookSecurity";

    /// <summary>
    /// Explicit debug flag to allow loopback / private destination endpoints for local testing only.
    /// Strictly forbidden and fails startup in Production environments.
    /// </summary>
    public bool AllowLoopbackForTesting { get; set; } = false;

    public bool RequireHttps { get; set; } = true;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxResponseBytes { get; set; } = 4096; // 4 KB excerpt limit
}

public sealed class WebhookUrlValidator : IWebhookUrlValidator
{
    private readonly WebhookSecurityOptions _options;

    public WebhookUrlValidator(IOptions<WebhookSecurityOptions> options)
    {
        _options = options.Value;
    }

    public async Task<UrlValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null)
        {
            return UrlValidationResult.Failure("URI cannot be null.");
        }

        if (!uri.IsAbsoluteUri)
        {
            return UrlValidationResult.Failure("URI must be absolute.");
        }

        // Scheme validation
        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (_options.RequireHttps && !isHttps)
        {
            if (!(_options.AllowLoopbackForTesting && isHttp))
            {
                return UrlValidationResult.Failure("Only HTTPS URLs are allowed.");
            }
        }
        else if (!isHttps && !isHttp)
        {
            return UrlValidationResult.Failure($"Unsupported URI scheme '{uri.Scheme}'.");
        }

        // User info rejection
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UrlValidationResult.Failure("User information (credentials) in URL is forbidden.");
        }

        // Port restrictions
        if (!uri.IsDefaultPort && uri.Port != 80 && uri.Port != 443 && uri.Port != 8080 && uri.Port != 8443)
        {
            if (!_options.AllowLoopbackForTesting)
            {
                return UrlValidationResult.Failure($"Port {uri.Port} is not in the permitted outbound ports list.");
            }
        }

        var host = uri.DnsSafeHost;

        // Loopback / direct IP check
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            if (_options.AllowLoopbackForTesting && IPAddress.IsLoopback(ipAddress))
            {
                return UrlValidationResult.Success();
            }

            if (IsProhibitedIpAddress(ipAddress))
            {
                return UrlValidationResult.Failure($"Direct connection to prohibited IP address {ipAddress} is forbidden.");
            }

            return UrlValidationResult.Success();
        }

        // DNS resolution check
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            if (addresses.Length == 0)
            {
                return UrlValidationResult.Failure($"Host '{host}' could not be resolved.");
            }

            foreach (var addr in addresses)
            {
                if (_options.AllowLoopbackForTesting && IPAddress.IsLoopback(addr))
                {
                    continue;
                }

                if (IsProhibitedIpAddress(addr))
                {
                    return UrlValidationResult.Failure($"Host '{host}' resolved to prohibited address range ({addr}). Destination rejected.");
                }
            }

            return UrlValidationResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UrlValidationResult.Failure($"DNS resolution failed for host '{host}': {ex.Message}");
        }
    }

    public static bool IsProhibitedIpAddress(IPAddress address)
    {
        // Handle IPv4-mapped IPv6 addresses (e.g. ::ffff:127.0.0.1)
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 (Current network)
            if (bytes[0] == 0) return true;

            // 10.0.0.0/8 (RFC1918 Private)
            if (bytes[0] == 10) return true;

            // 127.0.0.0/8 (Loopback)
            if (bytes[0] == 127) return true;

            // 169.254.0.0/16 (Link-local & Cloud Metadata: 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 172.16.0.0/12 (RFC1918 Private)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.0.2.0/24 (TEST-NET-1)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true;

            // 192.168.0.0/16 (RFC1918 Private)
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 198.51.100.0/24 (TEST-NET-2)
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;

            // 203.0.113.0/24 (TEST-NET-3)
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

            // 224.0.0.0/4 (Multicast)
            if (bytes[0] >= 224 && bytes[0] <= 239) return true;

            // 240.0.0.0/4 (Reserved / Future use)
            if (bytes[0] >= 240) return true;

            // 255.255.255.255 (Broadcast)
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;

            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::1 (Loopback)
            if (IPAddress.IPv6Loopback.Equals(address)) return true;

            // :: (Unspecified)
            if (IPAddress.IPv6None.Equals(address)) return true;

            // fe80::/10 (Link-local)
            if (address.IsIPv6LinkLocal) return true;

            // fc00::/7 (Unique local address / private)
            if ((address.GetAddressBytes()[0] & 0xFE) == 0xFC) return true;

            // ff00::/8 (Multicast)
            if (address.IsIPv6Multicast) return true;

            // 2001:db8::/32 (Documentation)
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return true;

            return false;
        }

        return true;
    }
}
