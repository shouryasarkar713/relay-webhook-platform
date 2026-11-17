# ADR-0021: Shared Cloud Data Protection Key Ring via Azure Blob Storage and Azure Key Vault

- **Status**: Accepted
- **Date**: 2026-11-17

## 1. Context

Relay encrypts customer webhook signing secrets at rest using ASP.NET Core Data Protection (`IEndpointSecretProtector`). 

In local development, Data Protection persisted its key ring to a local user folder (`~/.relay/dataprotection-keys`). In a cloud environment, this is fundamentally flawed:
1. `Relay.Api` and `Relay.Worker` execute in separate containers with ephemeral, isolated filesystems.
2. If `Relay.Api` protects a customer signing secret during endpoint registration, an independent `Relay.Worker` replica cannot unprotect the secret to sign an outgoing HTTP request unless both containers share the exact same key ring and master encryption key.
3. If container instances restart or new revisions deploy, local ephemeral files are destroyed, rendering previously stored secrets permanently undecryptable.

## 2. Decision

We implemented a durable, distributed Data Protection architecture combining **Azure Blob Storage** and **Azure Key Vault**:

1. **Shared Key Ring Storage in Private Azure Blob Storage**:
   - Both `Relay.Api` and `Relay.Worker` persist and synchronize Data Protection keys to an authenticated blob in a private storage account (`dataprotection-keys/keys.xml`).
   - Uses `Azure.Extensions.AspNetCore.DataProtection.Blobs`:
     ```csharp
     dpBuilder.PersistKeysToAzureBlobStorage(new Uri(blobUri), new DefaultAzureCredential());
     ```
   - Both API and Worker identities are granted `Storage Blob Data Contributor` RBAC on the storage account.
   - Public blob access is completely disabled (`allowBlobPublicAccess: false`).

2. **Master Key Encryption at Rest via Azure Key Vault**:
   - The key ring XML document stored in Blob Storage is encrypted at rest using an asymmetric RSA 2048 key (`relay-dp-key`) managed inside Azure Key Vault.
   - Uses `Azure.Extensions.AspNetCore.DataProtection.Keys`:
     ```csharp
     dpBuilder.ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), new DefaultAzureCredential());
     ```
   - API and Worker identities are granted `Key Vault Crypto User` RBAC on the Key Vault to wrap and unwrap encryption keys.

3. **Consistent Application Name**:
   - Both hosts explicitly set `.SetApplicationName("Relay.Platform")`. This guarantees that both services compute matching cryptographic purpose sub-keys across revisions and replicas.

4. **Local Fallback**:
   - If `DataProtection:BlobUri` is not configured (in local development or unit/integration tests), the application gracefully falls back to local filesystem storage, preserving friction-free local developer workflows.

## 3. Consequences

### Positive
- Fully shared cryptographic key ring across all API and Worker container replicas.
- Durable persistence immune to container restarts, scaling events, and revision rollouts.
- Zero plaintext keys stored anywhere in Blob Storage: the key ring is protected with hardware-security-grade Key Vault RSA encryption.
- Seamless developer experience: automatically activates in Azure when environment variables are supplied, falls back to local folder for offline testing.

### Negative / Trade-offs
- Key Vault operations incur small API call latency during container startup when the key ring is loaded into memory (cached locally in memory thereafter).
