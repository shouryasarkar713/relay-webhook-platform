# ADR-0011: Server-Side Request Forgery (SSRF) Defense and Secure Outbound HTTP Dispatch

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay delivers outbound HTTP POST requests to customer-specified URLs. Because tenants control the target URL, malicious actors could register URLs pointing to internal infrastructure, causing Server-Side Request Forgery (SSRF).

Key attack vectors include:
1. Connecting to loopback addresses (`127.0.0.1`, `::1`).
2. Scanning private internal corporate networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
3. Querying cloud instance metadata services (`169.254.169.254`, AWS/Azure/GCP metadata endpoints) to exfiltrate VM/pod credentials or tokens.
4. Exploiting DNS Rebinding (Time-of-Check to Time-of-Use / TOCTOU): An attacker provides a domain that resolves to an external public IP during initial URL validation, but returns an internal IP (`169.254.169.254` or `127.0.0.1`) when the HTTP client establishes the actual socket connection.
5. Exploiting HTTP redirects: The initial destination is public, but the server returns a 302 redirect pointing to `http://169.254.169.254/latest/meta-data/`.

## 2. Decision

We implemented a **Multi-Layered SSRF Defense Architecture** spanning registration-time validation, transport-layer socket pinning, and client hardening:

1. **Registration-Time URL & DNS Validation (`IWebhookUrlValidator`)**:
   - Webhook URLs must use `https://` (HTTP is rejected in production; configurable via `WebhookSecurityOptions.RequireHttps`).
   - Scheme, host, and port are checked against prohibited values.
   - Hostname is resolved via `Dns.GetHostAddressesAsync`. Every returned IP address is checked against prohibited IP address ranges:
     - IPv4 loopback (`127.0.0.0/8`)
     - IPv4 private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`)
     - IPv4 link-local & cloud metadata (`169.254.0.0/16`, including `169.254.169.254`)
     - Broadcast and multicast ranges (`224.0.0.0/4`, `255.255.255.255`)
     - IPv6 loopback (`::1`), link-local (`fe80::/10`), unique-local (`fc00::/7`), and IPv4-mapped IPv6 (`::ffff:x.x.x.x`).
   - If *any* resolved address falls within a prohibited range, registration is rejected with `400 Bad Request`.

2. **Transport-Level Socket Pinning Against DNS Rebinding (`SecureSocketsHttpHandlerFactory`)**:
   - Initial validation at registration does not prevent DNS rebinding attacks at dispatch time.
   - We configure a custom `SocketsHttpHandler.ConnectCallback`.
   - When initiating a socket connection, `ConnectCallback` resolves the host DNS, validates that the IP is not in any prohibited range, and **explicitly binds the socket connection directly to the validated IP address**.
   - If an attacker rotates DNS between check time and socket connect time, the connection is aborted immediately at the socket layer before any HTTP bytes are sent.

3. **HTTP Client Hardening**:
   - **Automatic Redirects Disabled**: `AllowAutoRedirect = false`. Redirects are treated as terminal HTTP responses and never automatically followed.
   - **Request Timeouts**: Strict 10-second timeout per outbound delivery attempt (`RequestTimeout`), backed by linked `CancellationTokenSource`.
   - **Response Read Limits**: Max 4 KB response excerpt read (`MaxResponseBytes = 4096`) to prevent memory exhaustion / zip-bomb attacks from malicious endpoints.
   - **Connection Pooling**: Controlled connection lifetimes (`PooledConnectionLifetime = 2m`, `MaxConnectionsPerServer = 20`) to prevent socket exhaustion.

## 3. Consequences

### Positive
- Fully neutralizes SSRF, cloud metadata exfiltration, and DNS rebinding attacks.
- Socket-layer IP pinning ensures TOCTOU immunity.
- Bounded memory and timeout guarantees ensure malicious endpoints cannot freeze or crash the worker process.

### Negative
- Local development testing requires setting `WebhookSecurity:AllowLoopbackForTesting = true` and `RequireHttps = false` in development/testing configuration.
