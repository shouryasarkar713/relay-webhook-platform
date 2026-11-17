# Relay — Azure Cost Reconciliation & FinOps Guide

## 1. Overview

This document provides a subscription-aware, verified cost reconciliation for deploying Relay in **Central India (Pune)**. Relay's cloud architecture is engineered to provide deterministic, predictable expenses by eliminating over-provisioned VMs and unconstrained autoscaling, making it ideal for portfolio demonstrations, internship evaluations, and enterprise proofs-of-concept.

---

## 2. Definitive SKU Selections & Architectural Justification

### 2.1 Database Decision: Azure SQL Basic (5 DTU, 2 GB) vs. General Purpose Serverless
- **The Worker Polling Invariant**: `Relay.Worker` maintains active polling loops for:
  - Pending transactional Outbox messages (every 500ms – 1s).
  - Scheduled webhook deliveries (every 1s – 5s).
  - Circuit breaker canary probe leases (every 10s).
  - Idle semaphore bulkhead cleanup (every 30s).
- **Why Serverless Auto-Pause Fails**: Azure SQL Serverless auto-pause requires **zero active database sessions** and **zero CPU consumption** for the entire auto-pause delay window (minimum 60 minutes). Because `Relay.Worker` maintains continuous database connection pool activity and periodic queries, **the database will never auto-pause while the worker is running**.
- **Financial Impact**: Continuous 24/7 execution of Serverless compute (0.5 minimum vCore) costs approximately ~$0.000145/vCore-sec, which totals **~$35 – $50/month** for compute alone, plus storage. Furthermore, when resuming from pause, Serverless incurs a 30–60 second cold-start delay that causes initial HTTP requests to timeout or throw connection errors.
- **The Selected Choice**: **Azure SQL Basic (5 DTU, 2 GB)**:
  - **Flat Low Rate**: ~$0.0068/hour = **~$4.99/month** (~₹415 INR/month).
  - **Always Online**: Zero resume latency; no cold-start timeouts.
  - **Compatibility**: Fully supports Read Committed Snapshot Isolation (RCSI) and Entra ID passwordless Managed Identity authentication.
  - **Billing Granularity**: Azure SQL DTU single databases active for less than one hour are billed for each hour they exist.

### 2.2 Messaging Decision: Azure Service Bus Standard Tier
- **Why Standard**: Required for the `relay-events` queue because Azure Service Bus **Basic tier does not support Duplicate Detection or Dead-Letter Queues**.
- **Pricing Structure**: Base charge of ~$0.0135/hour = **~$10.05/month** (~₹835 INR/month). Includes 13 million message operations free per month.

### 2.3 Compute Decision: Azure Container Apps (Consumption Tier)
- **Allocations**:
  - `ca-relay-api`: 0.5 vCPU, 1.0 GiB memory. Ingress external.
  - `ca-relay-worker`: 0.5 vCPU, 1.0 GiB memory. Ingress disabled.
- **Worker Scaling Invariant**: The worker requires `minReplicas = 1` because its SQL polling loop must remain active even when the Service Bus queue is empty. (Service Bus queue depth triggers autoscale for fan-out processing, but does not measure the SQL webhook delivery backlog).
- **Pricing Structure**: Active replicas are billed per-second for allocated vCPU and GiB.
  - Active vCPU: ~$0.000024/vCPU-sec (~$0.0216/hr for 0.5 vCPU).
  - Active Memory: ~$0.000003/GiB-sec (~$0.0054/hr for 1.0 GiB).
  - Combined 2 replicas (API + Worker): ~$0.054/hour = ~$38.88/month.
  - Monthly Free Grant: Azure provides 180,000 vCPU-seconds (~50 vCPU-hours) and 360,000 GiB-seconds free per subscription each month, deducting ~$2.16/month.
  - Net 24/7 ACA Cost: **~$36.72/month**.

---

## 3. Cost Reconciliation: Three Deployment Scenarios

All figures reflect Central India region list pricing (exclusive of applicable local GST/taxes).

### Scenario A — One-Hour Test Deployment (Smoke Test & Evaluation)
*Assumes all resources are provisioned, full 10-step smoke test suite is executed, and resource group is torn down after 60 minutes:*

| Resource | Provisioned SKU | Billing Model | Hourly Rate | 1-Hour Test Cost (USD) | 1-Hour Test Cost (INR) |
| :--- | :--- | :--- | :---: | :---: | :---: |
| **Azure Container Apps** | 2 × (0.5 vCPU, 1 GiB) | Per-second consumption | ~$0.0540/hr | $0.054 | ~₹4.50 |
| **Azure SQL Database** | Basic (5 DTU, 2 GB) | Hourly granularity minimum | ~$0.0068/hr | $0.007 | ~₹0.60 |
| **Azure Service Bus** | Standard Tier | Hourly base charge | ~$0.0135/hr | $0.014 | ~₹1.15 |
| **Azure Container Registry**| Basic (10 GB) | Daily rate prorated | ~$0.1670/day | $0.167 | ~₹13.90 |
| **Azure Storage (Keys & Web)**| Standard Hot LRS (2 accounts)| Storage & operations | Negligible | $0.010 | ~₹0.85 |
| **Azure Key Vault** | Standard Tier | Key cryptographic operations| Negligible | $0.005 | ~₹0.40 |
| **Azure Monitor** | Log Analytics & App Insights | First 5 GB/mo free | Free Tier | $0.000 | ₹0.00 |
| **Egress Bandwidth** | Outbound HTTPS | First 100 GB/mo free | Free Tier | $0.000 | ₹0.00 |
| **Total Scenario A** | | | | **~$0.26 USD** | **~₹22 INR** |

---

### Scenario B — Continuous Demonstration Environment (30 Days, 24/7)
*Assumes both API and Worker run continuously for 720 hours, with Service Bus, SQL Basic, and monitoring active:*

| Resource | Provisioned SKU | Billing Metric | Monthly Cost (USD) | Monthly Cost (INR) |
| :--- | :--- | :--- | :---: | :---: |
| **Azure Container Apps** | 2 replicas continuously active | 720 hrs compute minus free grant | ~$36.72 | ~₹3,050 |
| **Azure SQL Database** | Basic (5 DTU, 2 GB) | 720 hrs continuous availability | ~$4.99 | ~₹415 |
| **Azure Service Bus** | Standard Tier | Base charge + < 1M operations | ~$10.05 | ~₹835 |
| **Azure Container Registry**| Basic SKU | 30 days image storage | ~$5.00 | ~₹415 |
| **Azure Storage (Keys)** | Blob Storage (Data Protection) | < 100 MB XML key ring + operations | ~$0.15 | ~₹12 |
| **Azure Storage (Static Web)**| Blob Storage ($web) | React 19 static bundle | ~$0.15 | ~₹12 |
| **Azure Key Vault** | Standard Tier | RSA 2048 key unwrap operations | ~$0.10 | ~₹8 |
| **Azure Monitor** | Log Analytics + App Insights | < 2 GB/month (within 5 GB free) | $0.00 | ₹0 |
| **Total Scenario B** | | | **~$57.16 USD / month** | **~₹4,750 INR / month** |

---

### Scenario C — Intermittent Demonstration Environment (On-Demand Usage)
*Assumes ACR and Storage are maintained between demos ($5.30/mo), and compute/broker/SQL are spun up 30 minutes before evaluations and torn down afterwards (e.g., 5 demonstration sessions × 2 hours = 10 active hours/month):*

| Resource Component | Active Duration / Billing | Monthly Cost (USD) | Monthly Cost (INR) |
| :--- | :--- | :---: | :---: |
| **Container Apps (API + Worker)** | 10 active hours compute | ~$0.54 | ~₹45 |
| **Azure SQL Database** | 10 active hours (Basic 5 DTU) | ~$0.07 | ~₹6 |
| **Azure Service Bus** | 10 active hours base charge | ~$0.14 | ~₹12 |
| **ACR & Storage Accounts** | Persisted across 30 days | ~$5.30 | ~₹440 |
| **Azure Monitor & Key Vault** | Operational telemetry | ~$0.05 | ~₹4 |
| **Total Scenario C** | | **~$6.10 USD / month** | **~₹507 INR / month** |

---

## 4. Key FinOps Guidelines & Cost Guards

1. **Deterministic Teardown**:
   - Utilize `./deploy/scripts/teardown.sh demo --confirm-delete-all-resources` directly following portfolio reviews to avoid unintended idle charges.
2. **Key Vault Soft-Delete Retention**:
   - Deleting the resource group retains Key Vault soft-delete protection for 90 days at zero additional charge.
3. **Application Insights Ingestion Guard**:
   - Ingestion stays within the 5 GB/month free tier by retaining the default 30-day retention and disabling verbose tracing in production.
