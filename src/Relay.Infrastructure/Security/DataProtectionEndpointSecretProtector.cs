using Microsoft.AspNetCore.DataProtection;
using Relay.Application.Abstractions;

namespace Relay.Infrastructure.Security;

public sealed class DataProtectionEndpointSecretProtector : IEndpointSecretProtector
{
    private const string Purpose = "Relay.WebhookEndpoint.SigningSecret.v1";
    private readonly IDataProtector _protector;

    public DataProtectionEndpointSecretProtector(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        return _protector.Unprotect(protectedValue);
    }
}
