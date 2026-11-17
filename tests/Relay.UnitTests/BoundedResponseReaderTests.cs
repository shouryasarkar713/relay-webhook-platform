using System.Text;
using FluentAssertions;
using Relay.Infrastructure.Security;
using Xunit;

namespace Relay.UnitTests;

public sealed class BoundedResponseReaderTests
{
    [Fact]
    public async Task ReadExcerptAsync_WithOversizedContent_StopsAtMaxBytes()
    {
        // 100 KB payload
        var largeString = new string('A', 100 * 1024);
        using var content = new StringContent(largeString, Encoding.UTF8, "text/plain");

        var excerpt = await BoundedResponseReader.ReadExcerptAsync(content, 4096, CancellationToken.None);

        excerpt.Should().NotBeNull();
        excerpt!.Length.Should().Be(4096);
        excerpt.Should().Be(new string('A', 4096));
    }

    [Fact]
    public async Task ReadExcerptAsync_WithIncompleteUtf8Boundary_DecodesGracefully()
    {
        // Unicode character '€' (Euro sign) is 3 bytes in UTF-8: 0xE2, 0x82, 0xAC
        // We create a stream of 2 bytes (cutting off the third byte)
        var partialBytes = new byte[] { 0xE2, 0x82 };
        using var content = new ByteArrayContent(partialBytes);

        // MaxBytes = 2, so it reads the 2 bytes of the incomplete 3-byte character
        var excerpt = await BoundedResponseReader.ReadExcerptAsync(content, 2, CancellationToken.None);

        excerpt.Should().NotBeNull();
        // Since flush: false, the incomplete sequence should produce 0 chars instead of throwing DecoderFallbackException
        excerpt.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadExcerptAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var content = new StringContent("some text", Encoding.UTF8, "text/plain");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedResponseReader.ReadExcerptAsync(content, 1024, cts.Token));
    }

    [Fact]
    public async Task ReadExcerptAsync_WithEmptyContent_ReturnsEmptyString()
    {
        using var content = new StringContent(string.Empty);

        var excerpt = await BoundedResponseReader.ReadExcerptAsync(content, 1024, CancellationToken.None);

        excerpt.Should().Be(string.Empty);
    }
}
