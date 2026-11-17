# ADR-0022: Azure Networking, Ingress, and Outbound Egress

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay requires a secure networking architecture for both inbound traffic (API event ingestion and tenant operations) and outbound traffic (webhook delivery to tenant-registered HTTP endpoints across the public internet).

Key architectural and security considerations include:
1. **Inbound Surface Area**:
   - `Relay.Api` must be accessible over HTTPS by external API consumers and frontend clients.
   - `Relay.Worker` performs background outbox dispatching and delivery polling; it should never accept inbound network connections.
   - Database (Azure SQL), Service Bus, Key Vault, and Blob Storage contain sensitive state and credentials.
2. **Outbound Egress & SSRF Protection**:
   - Webhook delivery requires sending HTTP `POST` requests to arbitrary URLs supplied by tenants.
   - This introduces severe Server-Side Request Forgery (SSRF) risks, such as probing cloud metadata services (e.g., `169.254.169.254` or Azure IMDS endpoints), private IP ranges (RFC 1918: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), and loopback addresses (`127.0.0.1`).
3. **Infrastructure Cost vs Network Isolation Trade-Off**:
   - In enterprise production environments, fully private VNet integration with Private Endpoints and an Azure NAT Gateway with dedicated static outbound public IPs is the gold standard.
   - However, Azure NAT Gateways incur a continuous fixed hourly charge (~$32/month) plus data processing costs, and Private Endpoints add ~$7.30/month per endpoint. For a cost-conscious demonstration tier, running full VNet infrastructure with dedicated NAT Gateways can quickly exceed budget constraints.

## 2. Decision

We adopted a defense-in-depth networking architecture balancing cost, performance, and security:

1. **Ingress Control**:
   - `Relay.Api` enables public external ingress restricted to HTTPS (port 443 -> container 8080) with TLS 1.2+ enforced.
   - `Relay.Worker` disables ingress entirely (`ingress: null` in Bicep). It has no exposed ports or public IP.
   - Azure PaaS resources (Azure SQL, Service Bus, Key Vault, Storage) restrict access via Azure firewall rules, requiring Entra ID token-based authentication and denying anonymous public traffic.

2. **Application-Layer SSRF Defense-in-Depth**:
   - Rather than relying solely on network-level IP firewalls, Relay enforces rigorous pre-flight DNS and IP validation at the application layer via `SsrfSafeHttpClientHandler` (ADR-0011).
   - Before dispatching outbound HTTP requests:
     1. The target URI must use `https://` (or `http://` only in explicitly configured local development modes).
     2. The host is resolved via DNS.
     3. Every resolved IP address is validated against blocklists:
        - Azure Instance Metadata Service (`169.254.169.254`).
        - IPv4 private networks (RFC 1918: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
        - Loopback (`127.0.0.0/8`, `::1`), link-local (`169.254.0.0/16`, `fe80::/10`), carrier-grade NAT (`100.64.0.0/10`), and multicast addresses.
     4. Custom socket connection factories enforce that the physical TCP connection is established only to the pre-validated IP address, defeating DNS rebinding (TOCTOU) attacks.

3. **VNet & NAT Gateway Option for Production**:
   - For demo/development tiers, Azure Container Apps outbound egress uses the shared Azure infrastructure outbound IP pool, saving NAT Gateway and Private Endpoint charges while relying on Relay's strict application-level SSRF defenses.
   - For enterprise tier deployments, the Bicep template supports attaching a dedicated virtual network subnet to the Container Apps Environment and associating an Azure NAT Gateway with a static Public IP, enabling tenants to whitelist Relay's dedicated egress IP.

## 3. Consequences

### Positive
- Strict application-layer SSRF protection prevents exfiltration of Azure instance metadata or attacking internal container network interfaces.
- Zero inbound attack surface on background processing worker (`Relay.Worker`).
- Extremely cost-efficient demo configuration with zero idle NAT Gateway or Private Endpoint charges.
- Clear upgrade path to full enterprise VNet integration without changing application code.

### Negative / Trade-offs
- In the demo configuration without dedicated NAT Gateway, Relay's outbound requests originate from a dynamic pool of Azure datacenter IPs, preventing tenants from creating strict IP whitelist firewall rules for webhook delivery.
