# ADR-0003: Machine-to-Machine API Key Authentication Scheme

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay ingests events from automated backend systems, serverless functions, microservices, and client platforms. Unlike interactive end-user web applications that authenticate human users with OAuth 2.0 / OpenID Connect authorization code flows, machine-to-machine (M2M) event ingestion requires low-latency, deterministic, stateless authentication that is simple for developers to integrate using standard HTTP headers.

## 2. Problem

How should Relay authenticate machine-to-machine requests securely, efficiently, and with protection against credential theft, database compromise, timing attacks, and cross-tenant impersonation?

## 3. Decision

We implement a custom ASP.NET Core `AuthenticationHandler` supporting an `X-Api-Key` HTTP header with structured, cryptographically hashed keys.

### Key Format & Structure
Keys adhere to the structured format:
```text
rl_live_<public-prefix>_<secret>
```
Example: `rl_live_pk_7f3a9e2b_9c82415d31b04a8e29471f54c9e830a7d5`
- `rl_live_`: Constant environment discriminator (or `rl_test_` in test modes).
- `<public-prefix>`: 8–16 character high-entropy prefix used for direct index lookup in the database.
- `<secret>`: 32+ byte high-entropy cryptographically secure random token generated via `RandomNumberGenerator`.

### Storage & Verification Rules
1. **Never Store Plaintext**: The database stores only the `KeyPrefix` and the cryptographic hash `KeyHash = SHA256(secret)`. Even if the database is dumped, active secrets cannot be recovered.
2. **Constant-Time Comparison**: When validating the submitted secret hash against the stored hash, `CryptographicOperations.FixedTimeEquals` is used to prevent side-channel timing attacks.
3. **Prefix-Based O(1) Index Lookup**: The authentication handler extracts the prefix from the header and queries `SELECT ... WHERE KeyPrefix = @prefix`. This avoids hashing every key in the database or performing table scans.
4. **Lifecycle Enforcement**: The handler validates that:
   - `IsActive == true` on the associated `Tenant`.
   - `RevokedAtUtc == null` on the `ApiKey`.
   - `ExpiresAtUtc == null || ExpiresAtUtc > DateTimeOffset.UtcNow`.
5. **Claims & Tenant Context**: Upon successful authentication, the scheme populates a `ClaimsPrincipal` with `ClaimTypes.NameIdentifier` (`ApiKeyId`) and custom claim `relay:tenant_id` (`TenantId`). An `ITenantContext` scoped service extracts this claim, ensuring all downstream services access the authenticated tenant without reading raw headers or untrusted query parameters.
6. **Error Masking**: Authentication failures always return generic `HTTP 401 Unauthorized` without leaking whether the prefix was invalid, the secret was incorrect, or the key was revoked.

## 4. Alternatives Considered

1. **OAuth 2.0 Client Credentials Grant (JWT Bearer)**:
   - *Pros*: Standardized enterprise token exchange.
   - *Cons*: Requires an external Identity Provider (Keycloak, Azure AD B2C, Auth0) or complex in-house token issuance service. Clients must perform two HTTP round-trips (token request + ingestion request), increasing ingestion latency and failure modes for simple event producers.
2. **Basic Authentication (`user:password`)**:
   - *Pros*: Built-in standard.
   - *Cons*: Poor semantics for API keys; confusing username/password mapping, prone to insecure credential storage practices in client libraries.

## 5. Consequences & Trade-offs

### Positive
- Extremely low latency (single indexed database query + SHA-256 computation).
- Secure by default: Zero plaintext credentials stored in database or memory beyond request scope.
- Immune to timing attacks via fixed-time verification.
- Simple client developer experience (`X-Api-Key` header).

### Negative / Trade-offs
- Key revocation requires database lookup on every request. (In future phases, an in-memory sliding cache with distributed invalidation or Redis cache can reduce DB load).
- If the developer loses their secret, it cannot be recovered and must be rotated by generating a new key.
