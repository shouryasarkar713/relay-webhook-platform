# ADR-0008: Multi-Tenant Webhook Endpoint Management and Subscription Model

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

In Phase 1 and Phase 2, Relay accepted incoming events from producers and published them to Azure Service Bus. In Phase 3, Relay transitions into a full webhook delivery platform. Tenants must register external HTTP/HTTPS callback destinations (endpoints) and specify which event types they wish to receive (e.g., `invoice.paid`, `order.fulfilled`).

In multi-tenant SaaS environments, endpoint registration presents critical security and operational risks:
1. Cross-tenant leakage: Tenants must never discover, mutate, or receive events intended for another tenant.
2. Secret exposure: Webhook signing secrets must be generated securely and protected at rest, never leaked in list or detail responses.
3. Subscription filtering efficiency: The fan-out engine must efficiently query active endpoints matching incoming event types without scanning all endpoints in the system.

## 2. Decision

We implemented a dedicated **Webhook Endpoint Management & Subscription** domain model within Clean Architecture:

1. **Strict Multi-Tenant Scoping**:
   - `WebhookEndpoint` entities contain a mandatory `TenantId` foreign key.
   - All management endpoints (`POST /v1/endpoints`, `GET /v1/endpoints`, `GET /v1/endpoints/{id}`, `PUT /v1/endpoints/{id}`, `DELETE /v1/endpoints/{id}`) are guarded by the `ApiKeyAuthenticationHandler` and automatically scoped to `currentTenant.TenantId`.
   - Cross-tenant queries return `404 Not Found` rather than `403 Forbidden` to prevent resource enumeration attacks.

2. **Normalized Subscription Relational Model**:
   - `WebhookEndpointSubscription` models the relationship between `WebhookEndpoint` and individual event type strings (`EventType`).
   - A filtered unique index on `(EndpointId, EventType)` ensures an endpoint cannot duplicate subscriptions.
   - An index on `(TenantId, EventType)` enables fast lookup during event fan-out.

3. **Soft-Delete Deactivation Lifecycle**:
   - Deleting an endpoint via `DELETE /v1/endpoints/{id}` performs a soft deactivation (`IsActive = false`, `DisabledAtUtc = @Now`) rather than a hard cascade delete.
   - This preserves historical delivery and audit records (`WebhookDeliveries`, `WebhookDeliveryAttempts`) while immediately stopping subsequent event fan-out matching.

4. **Cryptographic Signing Secret One-Time Display**:
   - When an endpoint is created, a 32-byte cryptographically secure random secret is generated using `RandomNumberGenerator.GetBytes(32)` formatted as `whsec_<hex>`.
   - The secret is encrypted using ASP.NET Core Data Protection (`IEndpointSecretProtector`) and saved to `SigningSecretProtected`.
   - The plaintext secret is returned in the API response **strictly once** upon initial registration (`POST /v1/endpoints`). All subsequent `GET` requests return `signingSecret: null`.

## 3. Consequences

### Positive
- Strict tenant isolation guarantees zero cross-tenant endpoint exposure.
- Normalized subscription entities allow database query optimizer to execute indexed index-seeks during high-throughput message fan-out.
- One-time secret exposure adheres to the zero-trust security principle.

### Negative
- Customers who lose their signing secret cannot retrieve it from the UI or API; they must update or rotate the endpoint secret.
