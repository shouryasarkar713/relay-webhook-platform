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
- **Pricing Structure**: Base charge of ~$0.0135/hour = **~$9.72/month** (30 days / 720 hours) or **~$10.05/month** (31 days / 744 hours) (~₹810 – ₹835 INR/month). Includes 13 million message operations free per month.

### 2.3 Compute Decision: Azure Container Apps (Consumption Tier)
- **Allocations**:
  - `ca-relay-api`: 0.5 vCPU, 1.0 GiB memory. Ingress external.
  - `ca-relay-worker`: 0.5 vCPU, 1.0 GiB memory. Ingress disabled.
- **Combined Active Footprint (2 Replicas Minimum)**:
  - Total allocated compute: 1.0 vCPU and 2.0 GiB memory.
  - Active vCPU rate (Central India): $0.000024 / vCPU-sec ($0.0864 / hour for 1.0 vCPU).
  - Active Memory rate (Central India): $0.000003 / GiB-sec ($0.0216 / hour for 2.0 GiB).
  - Combined active rate: **$0.108 / hour** ($77.76 gross per 720-hour month).
  - Monthly Subscription Free Grant: Azure provides 180,000 vCPU-seconds and 360,000 GiB-seconds free per subscription each month (valued at $5.40 total).
  - Conservative net 24/7 active compute cost: **$72.36 / month** (720 hrs) or **$74.95 / month** (744 hrs).
- **Idle Qualification**:
  - When replicas receive no incoming requests and CPU/network usage drops below Azure's idle detection thresholds, compute drops to idle pricing ($0.000005 / vCPU-sec and $0.0000005 / GiB-sec = $0.0216 / hour).
  - Net 24/7 idle compute cost: **$14.47 / month** (720 hrs) or **$14.99 / month** (744 hrs).
  - *Note:* Relay.Worker periodically polls SQL for Outbox messages; actual continuous 24/7 cost will land between the idle rate ($14.47) and the active rate ($72.36).

---

## 3. Cost Reconciliation: Realistic Deployment Scenarios

All figures reflect Central India region list pricing (exclusive of applicable local GST/taxes).

### Scenario A — Conservative 24/7 Continuous Deployment (30 Days / 720 Hours)
*Assumes both API and Worker run 24/7 at active rates without scaling to zero:*

| Resource | Provisioned SKU | Billing Metric | Monthly Cost (USD) | Monthly Cost (INR) |
| :--- | :--- | :--- | :---: | :---: |
| **Azure Container Apps** | 2 replicas continuously active | 720 hrs active minus free grant | ~$72.36 | ~₹6,030 |
| **Azure SQL Database** | Basic (5 DTU, 2 GB) | 720 hrs continuous availability | ~$4.99 | ~₹415 |
| **Azure Service Bus** | Standard Tier | Base charge ($0.0135/hr × 720) + ops | ~$9.72 | ~₹810 |
| **Azure Container Registry**| Basic SKU | 30 days image storage ($0.167/day) | ~$5.01 | ~₹417 |
| **Azure Storage (Keys & Web)**| Standard Hot LRS (2 accounts)| < 1 GB + operations | ~$0.10 | ~₹8 |
| **Azure Key Vault** | Standard Tier | Key cryptographic operations | ~$0.05 | ~₹4 |
| **Azure Monitor** | Log Analytics + App Insights | First 5 GB/mo free grant | $0.00 | ₹0 |
| **Total Scenario A** | | | **~$92.23 USD / month** | **~₹7,684 INR / month** |

*Note: In a 31-day month (744 hours), total increases to **~$95.44 USD**.*

---

### Scenario B — 24/7 Continuous Deployment with Idle Qualifications (30 Days / 720 Hours)
*Assumes replicas qualify for idle pricing during periods of zero traffic:*

| Resource | Provisioned SKU | Billing Metric | Monthly Cost (USD) | Monthly Cost (INR) |
| :--- | :--- | :--- | :---: | :---: |
| **Azure Container Apps** | 2 replicas at idle rates | 720 hrs idle minus free grant | ~$14.47 | ~₹1,205 |
| **Azure SQL Database** | Basic (5 DTU, 2 GB) | 720 hrs continuous availability | ~$4.99 | ~₹415 |
| **Azure Service Bus** | Standard Tier | Base charge ($0.0135/hr × 720) | ~$9.72 | ~₹810 |
| **Azure Container Registry**| Basic SKU | 30 days image storage | ~$5.01 | ~₹417 |
| **Azure Storage & Key Vault**| Blob & Key Vault Standard | Operational storage & crypto | ~$0.15 | ~₹12 |
| **Total Scenario B** | | | **~$34.34 USD / month** | **~₹2,859 INR / month** |

---

### Scenario C — Intermittent 10-Hour Evaluation Session (Recommended FinOps Policy)
*Assumes the platform is provisioned for a 10-hour evaluation session and immediately torn down using `teardown.sh` (assuming no other workloads have consumed the subscription's monthly free grant):*

| Resource Component | Duration / Billing Basis | Session Cost (USD) | Session Cost (INR) |
| :--- | :--- | :---: | :---: |
| **Container Apps (API + Worker)** | 10 hours (36,000 vCPU-s < 180,000 free grant) | $0.00 | ₹0.00 |
| **Azure SQL Database** | 10 active hours ($0.00693/hr × 10) | $0.07 | ~₹6.00 |
| **Azure Service Bus** | 10 active hours base charge ($0.0135/hr × 10) | $0.14 | ~₹11.50 |
| **Azure Container Registry**| Basic SKU (1 full billing day minimum) | $0.17 | ~₹14.00 |
| **Storage & Key Vault** | Operational telemetry & secrets | < $0.01 | ~₹0.50 |
| **Total Scenario C** | **10-Hour Complete Demo Session** | **~$0.39 USD** | **~₹32.00 INR** |

---

### Scenario D — API Scale-Up Boundary (Sustained Ingestion Load)
*Evaluates financial impact when `ca-relay-api` autoscales from 1 replica to its maximum limit of 3 replicas under high burst traffic (while `ca-relay-worker` remains at 1 replica):*

- **Maximum Scale Allocation**:
  - API Replicas: 3 × (0.5 vCPU, 1.0 GiB) = 1.5 vCPU and 3.0 GiB RAM.
  - Worker Replicas: 1 × (0.5 vCPU, 1.0 GiB) = 0.5 vCPU and 1.0 GiB RAM.
  - Total Scaled Allocation: **2.0 vCPU and 4.0 GiB RAM**.
- **Scaled Hourly Compute Rate**:
  - vCPU: 2.0 × $0.0864 = $0.1728 / hr
  - Memory: 4.0 × $0.0216 = $0.0864 / hr
  - Total Scaled Compute: **$0.2592 / hour** (~₹21.60 INR / hr).
- **Burst Financial Impact**:
  - A 4-hour high-volume stress evaluation at maximum 3-replica scale adds only **~$1.04 USD** (~₹86 INR).
  - Continuous 24/7 execution at maximum scale for 30 days (unrealistic for a demo): ~$181.22 / month (compute only).

---

## 4. Key FinOps Guidelines & Cost Guards

1. **Baseline Minimum Invariant**:
   - The default configuration provisions exactly 1 API replica and 1 Worker replica. All baseline estimates assume these minimums unless load triggers scaling.
2. **Shared Subscription Free Grants**:
   - Container Apps monthly free grants (180,000 vCPU-seconds and 360,000 GiB-seconds) and Log Analytics free grants (5 GB/month) are shared globally across the entire Azure subscription.
3. **Deterministic Teardown**:
   - Utilize `./deploy/scripts/teardown.sh demo --confirm-delete-all-resources` directly following portfolio reviews to avoid unintended idle charges.
4. **Key Vault Soft-Delete Retention**:
   - Deleting the resource group retains Key Vault soft-delete protection for 90 days at zero additional charge.
5. **Application Insights Ingestion Guard**:
   - Ingestion stays within the 5 GB/month free tier by retaining the default 30-day retention and disabling verbose tracing in production.
