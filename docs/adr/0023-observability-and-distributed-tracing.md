# ADR-0023: Observability, Distributed Tracing, and Azure Monitor Integration

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay is an asynchronous, multi-hop distributed system. An event ingested through the REST API goes through several discrete stages across multiple components and boundaries:
1. Inbound HTTP `POST /v1/events` on `Relay.Api`.
2. Atomic write to `Events` and `OutboxMessages` in Azure SQL Database.
3. Background claim and dispatch by `Relay.Worker`'s Outbox dispatcher loop.
4. Message publication to Azure Service Bus queue `relay-events`.
5. Queue message receipt and fan-out by `Relay.Worker`'s fan-out consumer.
6. Atomic insertion of `Deliveries` into Azure SQL Database.
7. Background delivery worker polling, circuit breaker evaluation, semaphore bulkhead acquisition, and outbound HTTP `POST` to customer endpoint.
8. Delivery attempt recording and potential dead-letter or retry scheduling.

Diagnosing delivery latency, tracking failures, and debugging issues across this decoupled pipeline without end-to-end distributed tracing is extremely difficult. We need a unified observability model conforming to open industry standards that integrates natively with Microsoft Azure.

## 2. Decision

We implemented **OpenTelemetry with Azure Monitor Integration** across both `Relay.Api` and `Relay.Worker`:

1. **Standardized SDK**:
   - Integrated `Azure.Monitor.OpenTelemetry.AspNetCore` (1.6.0) across all hosts.
   - Observability is activated simply by configuring the standard `APPLICATIONINSIGHTS_CONNECTION_STRING` environment variable.
   - When the connection string is absent (e.g., local developer workstations or unit tests), telemetry exporters are gracefully omitted without throwing exceptions.

2. **End-to-End W3C TraceContext Propagation**:
   - Inbound HTTP requests extract the standard W3C `traceparent` and `tracestate` headers via ASP.NET Core instrumentation.
   - When writing to `OutboxMessages`, the ambient activity's `TraceId` and `SpanId` are captured and serialized into the outbox message metadata.
   - When publishing to Azure Service Bus, the Service Bus SDK propagates the trace context into the application properties of the `ServiceBusMessage`.
   - When processing the queue message, the worker creates a child span linked to the originating trace.
   - When dispatching outbound webhooks to customer endpoints, the outgoing HTTP request injects the active `traceparent` header, allowing customer systems to correlate Relay's deliveries with upstream event origins.

3. **Metrics & Health Probes**:
   - Standard ASP.NET Core and runtime OpenTelemetry metrics (CPU, memory, garbage collection, active HTTP connections).
   - Custom business metrics:
     - `relay.outbox.published_count`: Rate of messages published to Service Bus.
     - `relay.delivery.attempt_count`: Total delivery attempts categorized by status (`Success`, `TransientFailure`, `TerminalFailure`).
     - `relay.delivery.latency_ms`: Outbound HTTP delivery duration histogram.
     - `relay.circuit_breaker.state`: Active circuit breaker states per endpoint.
     - `relay.bulkhead.active_slots`: Current concurrency slot utilization per endpoint.

4. **Centralized Log Analytics & Application Insights**:
   - Both ACA containers stream console logs and structured JSON logs to a centralized Azure Log Analytics Workspace (`log-relay-<env>`).
   - Application Insights (`appi-relay-<env>`) collects live distributed traces, dependencies (Azure SQL, Service Bus, external HTTP calls), exceptions, and live metrics.

## 3. Consequences

### Positive
- Unified distributed trace graph connects the initial API ingestion call all the way to the customer webhook HTTP response.
- Cloud-native integration with Azure Application Insights provides instant dependency mapping, performance profiling, and failure triage out of the box.
- Standard OpenTelemetry instrumentation prevents vendor lock-in; collectors can be redirected to Grafana Tempo, Datadog, or Jaeger if needed.
- Zero runtime overhead when `APPLICATIONINSIGHTS_CONNECTION_STRING` is not configured in local development.

### Negative / Trade-offs
- Ingestion costs for Application Insights can grow under extreme throughput if sampling is not tuned. Production environments must configure adaptive sampling (e.g., 20-50% rate).
- Slight storage overhead in outbox and delivery database tables for storing trace identifiers.
