using FluentAssertions;
using Microsoft.Extensions.Options;
using Relay.Infrastructure.Security;
using Xunit;

namespace Relay.UnitTests;

public sealed class WebhookUrlValidatorTests
{
    private readonly WebhookUrlValidator _validator;

    public WebhookUrlValidatorTests()
    {
        var options = Options.Create(new WebhookSecurityOptions
        {
            RequireHttps = true,
            AllowLoopbackForTesting = false
        });
        _validator = new WebhookUrlValidator(options);
    }

    [Theory]
    [InlineData("http://example.com/webhook")] // Not HTTPS
    [InlineData("ftp://example.com/webhook")] // Invalid scheme
    [InlineData("https://user:pass@example.com/webhook")] // Userinfo
    [InlineData("https://localhost/webhook")] // Loopback host
    [InlineData("https://127.0.0.1/webhook")] // Loopback IPv4
    [InlineData("https://[::1]/webhook")] // Loopback IPv6
    [InlineData("https://10.0.0.1/webhook")] // Private RFC1918 10.0.0.0/8
    [InlineData("https://172.16.5.10/webhook")] // Private RFC1918 172.16.0.0/12
    [InlineData("https://192.168.1.1/webhook")] // Private RFC1918 192.168.0.0/16
    [InlineData("https://169.254.169.254/latest/meta-data")] // AWS/Azure Metadata IP
    [InlineData("https://[fe80::1]/webhook")] // Link-local IPv6
    [InlineData("https://[fc00::1]/webhook")] // Unique-local IPv6
    public async Task ValidateAsync_RejectsProhibitedUrls(string url)
    {
        var uri = new Uri(url);
        var result = await _validator.ValidateAsync(uri);
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateAsync_PermitsSafePublicIpAddress()
    {
        // 93.184.216.34 is example.com's public IP (doesn't require live DNS in sandboxed runner)
        var uri = new Uri("https://93.184.216.34/test-endpoint");
        var result = await _validator.ValidateAsync(uri);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenLoopbackAllowedForTesting_AcceptsLocalhost()
    {
        var devOptions = Options.Create(new WebhookSecurityOptions
        {
            RequireHttps = false,
            AllowLoopbackForTesting = true
        });
        var devValidator = new WebhookUrlValidator(devOptions);

        var uri = new Uri("http://localhost:8080/test");
        var result = await devValidator.ValidateAsync(uri);
        result.IsValid.Should().BeTrue();
    }
}
