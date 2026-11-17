using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.DTOs;
using Relay.Domain.Entities;
using Relay.Infrastructure;
using Relay.IntegrationTests.Fixtures;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class WebhookEndpointsApiIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly HttpClient _clientA;
    private readonly HttpClient _clientB;

    public WebhookEndpointsApiIntegrationTests(RelayApiFactory factory)
    {
        _factory = factory;
        _clientA = _factory.CreateClient();
        _clientA.DefaultRequestHeaders.Add("X-Api-Key", RelayApiFactory.ApiKeyA);

        _clientB = _factory.CreateClient();
        _clientB.DefaultRequestHeaders.Add("X-Api-Key", RelayApiFactory.ApiKeyB);
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        await db.WebhookDeliveryAttempts.ExecuteDeleteAsync();
        await db.WebhookDeliveries.ExecuteDeleteAsync();
        await db.WebhookEventFanouts.ExecuteDeleteAsync();
        await db.WebhookEndpointSubscriptions.ExecuteDeleteAsync();
        await db.WebhookEndpoints.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateEndpoint_ValidRequest_ReturnsCreatedAndExposesSecretExactlyOnce()
    {
        // Arrange
        var request = new CreateWebhookEndpointRequest(
            Name: "Order Webhooks",
            Url: "https://93.184.216.34/webhooks/orders",
            EventTypes: new[] { "order.created", "order.fulfilled" });

        // Act 1: POST /v1/endpoints
        var postResponse = await _clientA.PostAsJsonAsync("/v1/endpoints", request);
        postResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await postResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();
        created.Should().NotBeNull();
        created!.Id.Should().NotBeEmpty();
        created.Name.Should().Be("Order Webhooks");
        created.Url.Should().Be("https://93.184.216.34/webhooks/orders");
        created.EventTypes.Should().BeEquivalentTo("order.created", "order.fulfilled");
        created.IsActive.Should().BeTrue();
        created.SigningSecret.Should().StartWith("whsec_");

        // Act 2: GET /v1/endpoints/{id}
        var getResponse = await _clientA.GetAsync($"/v1/endpoints/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var fetched = await getResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(created.Id);
        fetched.SigningSecret.Should().BeNull("Signing secret MUST NEVER be returned on subsequent GET calls!");

        // Act 3: GET /v1/endpoints (List)
        var listResponse = await _clientA.GetAsync("/v1/endpoints");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await listResponse.Content.ReadFromJsonAsync<List<WebhookEndpointResponse>>();
        list.Should().ContainSingle(e => e.Id == created.Id);
        list![0].SigningSecret.Should().BeNull("Signing secret MUST NEVER be returned on LIST calls!");
    }

    [Fact]
    public async Task TenantIsolation_TenantCannotAccessAnotherTenantsEndpoint()
    {
        // Arrange: Create endpoint under Tenant A
        var request = new CreateWebhookEndpointRequest(
            Name: "Alpha Endpoint",
            Url: "https://93.184.216.34/alpha",
            EventTypes: new[] { "payment.captured" });

        var postResponse = await _clientA.PostAsJsonAsync("/v1/endpoints", request);
        var created = await postResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();

        // Act & Assert 1: Tenant B GET should return 404
        var getByB = await _clientB.GetAsync($"/v1/endpoints/{created!.Id}");
        getByB.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act & Assert 2: Tenant B LIST should not contain Tenant A's endpoint
        var listByB = await _clientB.GetAsync("/v1/endpoints");
        var itemsB = await listByB.Content.ReadFromJsonAsync<List<WebhookEndpointResponse>>();
        itemsB.Should().NotContain(e => e.Id == created.Id);

        // Act & Assert 3: Tenant B DELETE should return 404
        var deleteByB = await _clientB.DeleteAsync($"/v1/endpoints/{created.Id}");
        deleteByB.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeactivateEndpoint_SoftDeactivatesEndpoint()
    {
        // Arrange
        var request = new CreateWebhookEndpointRequest(
            Name: "Deactivate Test",
            Url: "https://93.184.216.34/deact",
            EventTypes: new[] { "user.deleted" });

        var postResponse = await _clientA.PostAsJsonAsync("/v1/endpoints", request);
        var created = await postResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();

        // Act: DELETE /v1/endpoints/{id}
        var deleteResponse = await _clientA.DeleteAsync($"/v1/endpoints/{created!.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert: Endpoint still exists but IsActive is false
        var getResponse = await _clientA.GetAsync($"/v1/endpoints/{created.Id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<WebhookEndpointResponse>();
        fetched!.IsActive.Should().BeFalse();
        fetched.DisabledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateEndpoint_SSRFViolation_ReturnsBadRequest()
    {
        // Try creating endpoint pointing to private internal IP
        var request = new CreateWebhookEndpointRequest(
            Name: "Malicious Endpoint",
            Url: "https://10.0.0.5/steal-data",
            EventTypes: new[] { "order.created" });

        var response = await _clientA.PostAsJsonAsync("/v1/endpoints", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        problem!.Errors.Should().ContainKey("Url");
    }
}
