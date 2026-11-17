# ADR-0001: Modular Monolith vs Microservices Architecture

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay is a cloud-native webhook delivery and event management platform. It must handle high-throughput event ingestion, durable persistence, duplicate detection, and asynchronous delivery to external endpoints.

When designing cloud-native systems, engineers frequently consider decomposing applications into microservices (e.g., Ingestion Service, Authentication Service, Outbox Service, Delivery Service, Notification Service). However, premature microservice decomposition introduces substantial distributed systems operational overhead: network serialization latency, distributed transactions (Sagas/2PC), partial network failures, independent CI/CD pipelines, container orchestration (Kubernetes), and complex observability requirements.

## 2. Problem

What architectural topology best balances clear separation of concerns, high throughput, operational simplicity, and testability for Relay's current and upcoming milestones without premature distributed systems complexity?

## 3. Decision

We will structure Relay as a **Modular Monolith** for the synchronous REST API (`Relay.Api`) paired with an independent **Worker Service** (`Relay.Worker`) for asynchronous background processing:

- `Relay.Domain`: Core domain models, value objects, and business rules without external dependencies.
- `Relay.Application`: Application use cases, DTOs, interfaces, and pipeline behaviors.
- `Relay.Infrastructure`: EF Core DbContext, database migrations, SQL Server persistence, and security hashing implementations.
- `Relay.Api`: ASP.NET Core host, controllers, authentication handlers, OpenAPI specification, and health endpoints.
- `Relay.Worker`: Background service host executing outbox dispatching and delivery workflows.

Both projects share the same application and domain abstractions while retaining independent scalability profiles (e.g., API scaling with HTTP traffic, Worker scaling with outbox queue depth).

## 4. Alternatives Considered

1. **Microservices from Day 1**:
   - *Pros*: Independent deployment cycles and granular language/framework flexibility.
   - *Cons*: Introduces distributed transaction challenges (dual-write between services), network hops on the critical ingestion path, complex local development requiring multiple container runtimes, and unnecessary operational burden for a focused product.
2. **Single Monolithic Web Process Hosting Background Timers**:
   - *Pros*: Single project to build and run.
   - *Cons*: Couples CPU/memory intensive webhook delivery (retries, timeouts, network I/O) directly with the high-priority synchronous event ingestion API. Background threads can starve HTTP request threads or crash the API process on memory exhaustion.

## 5. Consequences & Trade-offs

### Positive
- Strict module boundaries maintained by C# project reference rules (Domain cannot reference Infrastructure or API).
- High-performance in-memory method calls between application layers during synchronous event ingestion.
- Simplified local development and testing: single database, straightforward integration tests with `WebApplicationFactory`.
- Clear deployment path to Azure: `Relay.Api` and `Relay.Worker` can be deployed as separate Azure Container Apps scaling independently.

### Negative / Trade-offs
- Boundaries must be guarded strictly via code reviews and architectural unit tests to prevent tight coupling across module boundaries.
- Database schema changes require coordinated deployment between API and Worker.
