using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.DTOs;
using Relay.Domain.Entities;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.IntegrationTests.Fixtures;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class WebhookDeliveriesApiIntegrationTests : IAsyncLifetime
{
    private readonly RelayApiFactory _factory;
    private readonly HttpClient _clientA;
    private readonly HttpClient _clientB;

    public WebhookDeliveriesApiIntegrationTests(RelayApiFactory factory)
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
        await db.OutboxMessages.ExecuteDeleteAsync();
        await db.Events.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetDeliveries_ReturnsPagedDeliveries_ForAuthenticatedTenantOnly()
    {
        // Arrange: Seed delivery for Tenant A and delivery for Tenant B
        var tenantAId = Guid.Parse(RelayApiFactory.TenantAId);
        var tenantBId = Guid.Parse(RelayApiFactory.TenantBId);
        var now = DateTimeOffset.UtcNow;

        var epA = new WebhookEndpoint(Guid.NewGuid(), tenantAId, "EP A", "https://93.184.216.34/hookA", "prot_sec_a", now);
        var evtA = new Event(Guid.NewGuid(), tenantAId, "order.placed", """{"id":1}""", "hash_a", "idem_a", now);
        var delivA = new WebhookDelivery(Guid.NewGuid(), evtA.Id, epA.Id, tenantAId, now);

        var epB = new WebhookEndpoint(Guid.NewGuid(), tenantBId, "EP B", "https://93.184.216.34/hookB", "prot_sec_b", now);
        var evtB = new Event(Guid.NewGuid(), tenantBId, "order.placed", """{"id":2}""", "hash_b", "idem_b", now);
        var delivB = new WebhookDelivery(Guid.NewGuid(), evtB.Id, epB.Id, tenantBId, now);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.WebhookEndpoints.AddRange(epA, epB);
            db.Events.AddRange(evtA, evtB);
            db.WebhookDeliveries.AddRange(delivA, delivB);
            await db.SaveChangesAsync();
        }

        // Act: Tenant A queries deliveries
        var responseA = await _clientA.GetAsync("/v1/deliveries");
        responseA.StatusCode.Should().Be(HttpStatusCode.OK);

        var pageResultA = await responseA.Content.ReadFromJsonAsync<PagedResult<WebhookDeliveryDto>>();
        pageResultA.Should().NotBeNull();
        pageResultA!.Count.Should().Be(1);
        pageResultA.Items.Should().ContainSingle(d => d.Id == delivA.Id);
        pageResultA.Items.Should().NotContain(d => d.Id == delivB.Id);

        // Act: Tenant B queries deliveries
        var responseB = await _clientB.GetAsync("/v1/deliveries");
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);

        var pageResultB = await responseB.Content.ReadFromJsonAsync<PagedResult<WebhookDeliveryDto>>();
        pageResultB.Should().NotBeNull();
        pageResultB!.Count.Should().Be(1);
        pageResultB.Items.Should().ContainSingle(d => d.Id == delivB.Id);
    }

    [Fact]
    public async Task GetDeliveryById_CrossTenantAccess_Returns404NotFound()
    {
        // Arrange: Delivery belonging to Tenant A
        var tenantAId = Guid.Parse(RelayApiFactory.TenantAId);
        var now = DateTimeOffset.UtcNow;
        var epA = new WebhookEndpoint(Guid.NewGuid(), tenantAId, "EP A", "https://93.184.216.34/hookA", "prot_sec_a", now);
        var evtA = new Event(Guid.NewGuid(), tenantAId, "order.placed", """{"id":1}""", "hash_a", "idem_a", now);
        var delivA = new WebhookDelivery(Guid.NewGuid(), evtA.Id, epA.Id, tenantAId, now);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
            db.WebhookEndpoints.Add(epA);
            db.Events.Add(evtA);
            db.WebhookDeliveries.Add(delivA);
            await db.SaveChangesAsync();
        }

        // Act: Tenant B attempts to fetch Tenant A's delivery
        var response = await _clientB.GetAsync($"/v1/deliveries/{delivA.Id}");

        // Assert: 404 NotFound
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
