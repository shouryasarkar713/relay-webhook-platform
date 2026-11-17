using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Relay.Infrastructure.Security;
using Xunit;

namespace Relay.UnitTests;

public sealed class EndpointSecretProtectorTests
{
    private readonly DataProtectionEndpointSecretProtector _protector;

    public EndpointSecretProtectorTests()
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Relay.Tests");

        var provider = services.BuildServiceProvider();
        _protector = new DataProtectionEndpointSecretProtector(provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public void ProtectAndUnprotect_RoundtripsSuccessfully()
    {
        var rawSecret = "whsec_0123456789abcdef0123456789abcdef";
        var protectedValue = _protector.Protect(rawSecret);

        protectedValue.Should().NotBeNullOrEmpty();
        protectedValue.Should().NotBe(rawSecret);

        var recovered = _protector.Unprotect(protectedValue);
        recovered.Should().Be(rawSecret);
    }

    [Fact]
    public void Unprotect_WithTamperedCiphertext_ThrowsCryptographicException()
    {
        var rawSecret = "whsec_secret";
        var protectedValue = _protector.Protect(rawSecret);
        var tampered = protectedValue.Substring(0, protectedValue.Length - 5) + "AAAAA";

        var act = () => _protector.Unprotect(tampered);
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}
