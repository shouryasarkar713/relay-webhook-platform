using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Relay.Infrastructure.Security;

public static class SecureSocketsHttpHandlerFactory
{
    public static SocketsHttpHandler Create(
        IOptions<WebhookSecurityOptions> options,
        ILogger logger)
    {
        var opt = options.Value;

        var handler = new SocketsHttpHandler
        {
            // Security: Disable automatic redirects completely to prevent SSRF via redirect to internal IP
            AllowAutoRedirect = false,

            // Performance & timeouts
            ConnectTimeout = TimeSpan.FromSeconds(5),

            // Connection pooling controls
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 20
        };

        // Pin socket connection to validated IP to defend against DNS rebinding (TOCTOU)
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;

            IPAddress[] addresses;

            if (IPAddress.TryParse(host, out var parsedIp))
            {
                addresses = new[] { parsedIp };
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }

            if (addresses.Length == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            // Find first permitted IP
            IPAddress? selectedAddress = null;
            foreach (var addr in addresses)
            {
                if (opt.AllowLoopbackForTesting && IPAddress.IsLoopback(addr))
                {
                    selectedAddress = addr;
                    break;
                }

                if (!WebhookUrlValidator.IsProhibitedIpAddress(addr))
                {
                    selectedAddress = addr;
                    break;
                }
            }

            if (selectedAddress is null)
            {
                logger.LogError("SSRF Blocked: All resolved IP addresses for host '{Host}' are in prohibited ranges.", host);
                throw new InvalidOperationException($"SSRF Blocked: Connection to '{host}' was refused because it resolved to prohibited IP address(es).");
            }

            var socket = new Socket(selectedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(selectedAddress, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        return handler;
    }
}
