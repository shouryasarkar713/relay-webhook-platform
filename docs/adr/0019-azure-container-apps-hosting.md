# ADR-0019: Azure Container Apps for API and Worker Hosting

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay requires an execution environment to host two distinct executable services:
1. `Relay.Api`: An ASP.NET Core REST API handling public HTTP event ingestion, endpoint registration, and delivery log queries.
2. `Relay.Worker`: A long-running .NET background service running transactional outbox dispatching, Service Bus fan-out consumption, and webhook HTTP delivery dispatching.

We evaluated three cloud hosting models:
- **Azure App Service (Linux Web Apps)**: Mature PaaS, but running independent background workers alongside web apps either requires deploying two separate App Service Plans or running background workers inside WebJobs (which is less idiomatic for modern .NET 10 micro-services).
- **Azure Kubernetes Service (AKS)**: Maximum flexibility, but introduces immense operational complexity (control plane management, ingress controllers, Helm charts, node pools) that is disproportionate for this portfolio application.
- **Azure Container Apps (ACA)**: Fully managed serverless container runtime built on Kubernetes (K8s/KEDA/Envoy) without exposing cluster infrastructure management.

## 2. Decision

We selected **Azure Container Apps** for hosting both `Relay.Api` and `Relay.Worker`:

1. **Independent Container Apps in a Single Environment**:
   - Both apps share a single `ManagedEnvironment` (`cae-relay-<env>`), sharing Log Analytics and virtual network infrastructure.
   - `Relay.Api`: Configured with external HTTP ingress on port 8080, HTTPS-only, HTTP/2 and auto-transport, with liveness (`/health/live`) and readiness (`/health/ready`) probes.
   - `Relay.Worker`: Configured with **no ingress**, running as an internal processing daemon.

2. **Scaling Strategy & Zero-Scale Guard**:
   - `Relay.Api` scales horizontally based on concurrent HTTP requests (threshold: 100 concurrent requests).
   - `Relay.Worker` requires a **minimum replica count of 1**. Because the worker polls SQL Server for pending deliveries and dead-letter retry timeouts, scaling to 0 would leave pending deliveries stalled when no new Service Bus messages arrive.
   - For workloads with high Service Bus queue backlog, KEDA Service Bus scaling rules can scale worker replicas up to a maximum of 3.

3. **Multi-Stage Non-Root Containers**:
   - Built using official Microsoft .NET 10 images (`mcr.microsoft.com/dotnet/aspnet:10.0` and `mcr.microsoft.com/dotnet/runtime:10.0`).
   - Containers execute as non-root (`USER $APP_UID`), adhering to least-privilege security standards.

## 3. Consequences

### Positive
- Fully managed serverless container runtime with zero VM or cluster maintenance overhead.
- Native integration with Azure Monitor, Application Insights, Log Analytics, and Managed Identities.
- Built-in traffic splitting and revision management for zero-downtime deployments.
- Pay-per-second consumption billing model keeps costs predictable and minimal for portfolio demonstration.

### Negative / Trade-offs
- Cold-start latency if replicas scale down to 0; mitigated by enforcing `minReplicas = 1` for the critical worker daemon.
- Process-local semaphore bulkheads multiply by the number of active worker replicas.
