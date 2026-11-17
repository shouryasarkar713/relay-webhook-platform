using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Relay.IntegrationTests.Fixtures;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class HealthCheckIntegrationTests 
{
    private readonly HttpClient _client;

    public HealthCheckIntegrationTests(RelayApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LiveHealthEndpoint_Returns200AndLiveStatus()
    {
        // Act
        var response = await _client.GetAsync("/health/live");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("Live");
    }

    [Fact]
    public async Task ReadyHealthEndpoint_WhenDatabaseConnected_Returns200AndReadyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("Ready");
        json.GetProperty("database").GetString().Should().Be("Connected");
    }
}
