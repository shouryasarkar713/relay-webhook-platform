# Relay — Operational Runbook

## 1. Overview

This document provides operational procedures, incident response workflows, and maintenance guides for managing Relay in Microsoft Azure.

---

## 2. Health Monitoring & Diagnostics

### 2.1 Health Check Endpoints
`Relay.Api` exposes two standard health endpoints:
- **Liveness Probe**: `GET /health/live`
  - Returns `200 OK` if the ASP.NET Core process is responsive.
  - Used by Container Apps to restart frozen container instances.
- **Readiness Probe**: `GET /health/ready`
  - Validates connectivity to Azure SQL Database and Azure Key Vault.
  - Returns `200 OK` with detailed component statuses (`Healthy`, `Degraded`, `Unhealthy`).
  - Container Apps routes traffic only to replicas reporting `Healthy`.

### 2.2 Key Operational Metrics
Monitor the following metrics in Application Insights and Azure Monitor:
- `HttpRequestsTotal` / `HttpResponseDuration`: Inbound API traffic and latency.
- `relay.outbox.published_count`: Outbox dispatcher throughput. If this drops to zero while `OutboxMessages` has pending records, investigate worker logs.
- `relay.delivery.attempt_count` (tagged by status: `Success`, `TransientFailure`, `TerminalFailure`): Measures webhook delivery success rates.
- `relay.circuit_breaker.state`: Indicates tripped circuit breakers on failing customer endpoints.
- Azure Service Bus: `ActiveMessages`, `DeadLetteredMessages`.

---

## 3. Routine Operations

### 3.1 Onboarding a New Tenant
To register a new tenant, use the `relay-admin` CLI tool:
```bash
# Run via local environment or administrative container
dotnet run --project src/Relay.Admin -- tenant create "Acme Corp"
```
*Output:*
```text
Tenant created successfully:
  ID:       3fa85f64-5717-4562-b3fc-2c963f66afa6
  Name:     Acme Corp
  Status:   Active
```

### 3.2 Generating an API Key for a Tenant
```bash
dotnet run --project src/Relay.Admin -- apikey create 3fa85f64-5717-4562-b3fc-2c963f66afa6 "Production Ingestion Key"
```
*Output:*
```text
API Key generated successfully:
  Tenant ID:   3fa85f64-5717-4562-b3fc-2c963f66afa6
  Description: Production Ingestion Key
  Plain Key:   rel_live_a8f9c2d1e4b3... (SAVE THIS NOW: It will never be shown again)
```

### 3.3 Revoking a Compromised API Key
```bash
dotnet run --project src/Relay.Admin -- apikey revoke <keyId>
```

### 3.4 Applying Database Schema Migrations
Database migrations are applied deterministically via `relay-admin`:
```bash
dotnet run --project src/Relay.Admin -- migrate
```

---

## 4. Incident Response & Troubleshooting

### 4.1 Issue: Outbox Dispatcher Stall (Pending Messages Accumulating)
- **Symptoms**: Events are ingested via API (`202 Accepted`), but deliveries are not being created; `OutboxMessages` status remains `Pending` or `Claimed`.
- **Diagnosis**:
  1. Check Azure Service Bus status: Is the namespace active or throttled?
  2. Inspect Worker container logs in Log Analytics:
     ```kusto
     ContainerAppConsoleLogs_CL
     | where ContainerAppName_s == "ca-relay-worker"
     | where Log_s has "OutboxDispatcher" or Log_s has "Exception"
     | order by TimeGenerated desc
     | take 50
     ```
  3. Check for stale leases in SQL:
     ```sql
     SELECT COUNT(*) FROM OutboxMessages WHERE Status = 'Claimed' AND LeaseExpiresAtUtc < SYSUTCDATETIME();
     ```
- **Remediation**:
  - The worker background sweeper automatically reclaims leases older than 30 seconds.
  - If the worker container is crashed or halted, restart the worker container revision in Azure Portal or CLI:
    ```bash
    az containerapp revision restart --name ca-relay-worker --resource-group rg-relay-prod
    ```

### 4.2 Issue: Spike in Dead-Lettered Deliveries
- **Symptoms**: Customer reports missing events; `Deliveries` table contains records with status `DeadLettered`.
- **Diagnosis**:
  1. Inspect the most recent delivery attempt:
     ```sql
     SELECT Top 10 d.Id, d.EndpointId, d.Status, d.FailureReason, a.HttpStatus, a.ResponseBodySnippet
     FROM Deliveries d
     OUTER APPLY (
         SELECT TOP 1 HttpStatus, ResponseBodySnippet
         FROM DeliveryAttempts a
         WHERE a.DeliveryId = d.Id
         ORDER BY a.AttemptNumber DESC
     ) a
     WHERE d.Status = 'DeadLettered'
     ORDER BY d.CompletedAtUtc DESC;
     ```
  2. If `HttpStatus` is `401 Unauthorized` or `404 Not Found`, the customer's webhook endpoint is misconfigured.
  3. If `HttpStatus` is `500 Internal Server Error` or network timeout, the customer's server experienced an outage.
- **Remediation**:
  - Once the customer resolves their endpoint outage, trigger a batch replay via API:
    ```bash
    curl -X POST https://api.relay.example.com/v1/deliveries/replay-dead-letters \
      -H "X-Api-Key: <tenantApiKey>" \
      -H "Content-Type: application/json" \
      -d '{"endpointId": "<endpointId>", "maxCount": 100}'
    ```

### 4.3 Issue: Circuit Breaker Stuck in Open State
- **Symptoms**: Endpoint status shows `CircuitBreakerOpen`; all incoming deliveries immediately fail or postpone.
- **Diagnosis**:
  - Relay automatically schedules canary probes after the circuit breaker cooldown period (default: 60 seconds).
  - If the customer endpoint continues to return 5xx errors or timeouts, the probe fails and extends the open period.
- **Remediation**:
  - Verify if the customer endpoint is healthy by querying its URL directly or checking external synthetic monitors.
  - If the customer fixed their endpoint and wants immediate reset, an administrative reset can be triggered via the endpoint update endpoint.

---

## 5. Scaling & Resource Adjustments

### 5.1 Manual Replica Scaling
To handle planned traffic spikes:
```bash
# Scale API replicas to min 2, max 5
az containerapp update \
  --name ca-relay-api \
  --resource-group rg-relay-prod \
  --min-replicas 2 \
  --max-replicas 5
```

### 5.2 Azure SQL Tier Upgrade
If database CPU reaches sustained >80%:
```bash
az sql db update \
  --resource-group rg-relay-prod \
  --server sql-relay-prod \
  --name sqldb-relay \
  --service-objective S2
```
