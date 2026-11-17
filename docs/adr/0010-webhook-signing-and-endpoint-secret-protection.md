# ADR-0010: Webhook Cryptographic Signing and Secret Protection at Rest

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Webhook payloads are transmitted across the public Internet to customer servers. Without cryptographic verification:
1. Attackers can forge HTTP POST requests pretending to originate from Relay.
2. Man-in-the-middle attackers could alter webhook payloads in transit.
3. Attackers could replay previously captured webhook requests.

Furthermore, if customer signing secrets are stored in plaintext in the database, a database leak would compromise all tenant signing keys.

## 2. Decision

We implemented a **HMAC-SHA256 Canonical Signing Protocol** and **Data Protection Encryption at Rest**:

1. **HMAC-SHA256 Signature Specification**:
   - Every outgoing webhook delivery includes two security headers:
     - `X-Relay-Timestamp`: Unix epoch timestamp in seconds (`t`).
     - `X-Relay-Signature`: Canonical signature header formatted as:
       ```text
       t=<unix_timestamp>,v1=<hex_hmac_sha256>
       ```
   - Canonical signature input format:
     ```text
     {timestamp}.{raw_json_request_body}
     ```
   - Computed using HMAC-SHA256 keyed with the endpoint's raw signing secret.
   - Verification uses constant-time comparison (`CryptographicOperations.FixedTimeEquals`) to eliminate timing side-channel attacks.
   - Timestamp tolerance prevents replay attacks (default 5-minute validity window).

2. **Secret Encryption at Rest**:
   - Customer endpoint secrets are **never** stored in plaintext.
   - We utilize ASP.NET Core Data Protection (`IDataProtectionProvider`) with authenticated AES-256-GCM encryption.
   - The application name is explicitly pinned to `Relay.Platform`, and key storage is configured to a shared directory (`~/.relay/dataprotection-keys`) across API and Worker hosts.
   - The entity property `SigningSecretProtected` stores only ciphertexts.
   - Only the background delivery dispatcher decrypts the secret at dispatch time in memory.

3. **Standard Canonical Webhook Envelope**:
   - Webhook payloads delivered to endpoints follow an explicit JSON envelope:
     ```json
     {
       "schemaVersion": 1,
       "eventId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
       "eventType": "invoice.paid",
       "occurredAtUtc": "2026-11-17T03:00:00Z",
       "payload": { ... }
     }
     ```

## 3. Consequences

### Positive
- Prevents tampering, impersonation, and replay attacks.
- Follows industry best practices established by Stripe, GitHub, and Shopify.
- Compromise of SQL Server read replicas does not expose plaintext customer signing secrets.

### Negative
- Customers must implement HMAC-SHA256 verification in their webhook receivers.
- Both API and Worker hosts must share identical Data Protection key rings.
