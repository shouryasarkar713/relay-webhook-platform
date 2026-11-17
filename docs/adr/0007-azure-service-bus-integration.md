# ADR-0007: Azure Service Bus Integration and Message Publishing Architecture

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay decouples HTTP event ingestion from asynchronous webhook delivery via a persistent message broker. In accordance with the cloud-native Azure architecture, Azure Service Bus (Standard/Premium) provides the enterprise messaging backbone, offering message deduplication, pub/sub topics, and native integration with Azure infrastructure.

## 2. Problem

How should the application publish outbox events to Azure Service Bus while upholding Clean Architecture principles, ensuring idempotent message broker deduplication, avoiding SDK leakage into the domain, and facilitating local development and automated testing?

## 3. Decision

1. **Clean Architecture Boundary**:
   - `Relay.Domain` and `Relay.Application` have zero dependencies on Azure SDK packages (`Azure.Messaging.ServiceBus`, `Azure.Identity`).
   - Defined `IEventMessagePublisher` and `OutboxEnvelope` contracts in `Relay.Application.Messaging`.
   - Implemented `AzureServiceBusEventPublisher` in `Relay.Infrastructure.Messaging`.

2. **Deduplication and Idempotent Publishing Requirements**:
   - Every `ServiceBusMessage` published sets:
     - `MessageId = envelope.EventId.ToString("D")`
     - `CorrelationId = envelope.EventId.ToString("D")`
     - `Subject = envelope.EventType`
     - `ContentType = "application/json"`
   - **Crucial Architectural Invariant**: Setting `MessageId` alone does NOT enable broker deduplication. Broker-side duplicate detection requires explicit queue/topic entity configuration:
     - `RequiresDuplicateDetection = true`
     - `DuplicateDetectionHistoryTimeWindow` (e.g. 5 minutes on emulator, up to 7 days on Azure Standard/Premium).
     - **Note**: Duplicate detection cannot be enabled on an existing queue after creation; if a queue was created without it, it must be recreated.
     - **At-Least-Once Delivery**: Broker deduplication applies strictly to sends within the detection window. It does not provide eternal deduplication or guarantee exactly-once processing at consumer endpoints. Downstream webhook consumers must implement idempotency.

3. **Envelope Serialization Integrity**:
   - The original event payload JSON is preserved without double-escaping or stringification by binding it as a `JsonElement` on the typed envelope (`OutboxEnvelope`), ensuring clean JSON serialization for downstream consumers.

4. **Dual Authentication Support**:
   - Local development and test environments can authenticate using a standard connection string (compatible with the official Service Bus emulator).
   - Cloud environments authenticate via Managed Identity (`FullyQualifiedNamespace` with `DefaultAzureCredential`), eliminating long-lived credentials.

5. **Diagnostic Simulation Publisher Safety**:
   - The diagnostic logging publisher (`SimulationDiagnosticPublisher`) is strictly quarantined: it requires explicit opt-in (`ServiceBus:AllowSimulationPublisher=true`) and runs ONLY in the `Development` environment. Production startup immediately fails with a fatal configuration exception if Azure Service Bus credentials or namespace are missing, preventing silent data loss or simulated publishing from masquerading as actual delivery.

6. **Real Broker Verification Status**:
   - Verified end-to-end against the official Azure Service Bus Emulator container (`mcr.microsoft.com/azure-messaging/servicebus-emulator:latest`) and local SQL Server 2022 instance on Linux/Podman.
   - Verified that sending identical `MessageId` messages results in the broker suppressing the second duplicate send during the duplicate detection window.

## 4. Consequences & Trade-offs

### Positive
- Strict isolation of cloud provider messaging SDK from domain and application logic.
- Broker-level duplicate detection enabled via deterministic `MessageId`.
- Preserves native JSON payloads for consumer ease-of-use.
- Supports Azure Managed Identity for zero-trust cloud deployments.

### Negative / Trade-offs
- Azure Service Bus client instances are designed to be singletons; connection pooling and lifetime management must be handled carefully to avoid TCP socket exhaustion.
