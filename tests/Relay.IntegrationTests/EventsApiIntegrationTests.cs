using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.DTOs;
using Relay.Domain.Enums;
using Relay.Infrastructure;
using Relay.IntegrationTests.Fixtures;
using Xunit;

namespace Relay.IntegrationTests;

[Collection("DatabaseCollection")]
public sealed class EventsApiIntegrationTests 
{
    private readonly RelayApiFactory _factory;
    private readonly HttpClient _client;

    public EventsApiIntegrationTests(RelayApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Scenario01_UnauthenticatedEventIngestion_Returns401Unauthorized()
    {
        // Arrange
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-1"}""").RootElement);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", "idem_unauth_test");
        // No X-Api-Key header

        // Act
        var response = await _client.SendAsync(httpRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scenario01b_RevokedApiKey_Returns401Unauthorized()
    {
        // Arrange
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-1"}""").RootElement);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyRevoked);
        httpRequest.Headers.Add("Idempotency-Key", "idem_revoked_test");

        // Act
        var response = await _client.SendAsync(httpRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scenario01c_ExpiredApiKey_Returns401Unauthorized()
    {
        // Arrange
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-1"}""").RootElement);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyExpired);
        httpRequest.Headers.Add("Idempotency-Key", "idem_expired_test");

        // Act
        var response = await _client.SendAsync(httpRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scenario01d_InactiveTenantKey_Returns401Unauthorized()
    {
        // Arrange
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-1"}""").RootElement);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyInactive);
        httpRequest.Headers.Add("Idempotency-Key", "idem_inactive_test");

        // Act
        var response = await _client.SendAsync(httpRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Scenario02_ValidEventIngestion_Returns202AcceptedAndLocationHeader()
    {
        // Arrange
        var idempotencyKey = $"idem_valid_{Guid.NewGuid()}";
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-202", "amount": 250.0}""").RootElement);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);

        // Act
        var response = await _client.SendAsync(httpRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        body.Should().NotBeNull();
        body!.EventId.Should().NotBeEmpty();
        body.Status.Should().Be("Accepted");

        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Contain($"/v1/events/{body.EventId}");
    }

    [Fact]
    public async Task Scenario03_And_04_EventAndOutboxRecord_PersistedAtomicallyInDatabase()
    {
        // Arrange
        var idempotencyKey = $"idem_atomic_{Guid.NewGuid()}";
        var request = new IngestEventRequest("payment.captured", JsonDocument.Parse("""{"paymentId": "PAY-99", "amount": 99.0}""").RootElement);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);

        // Act
        var response = await _client.SendAsync(httpRequest);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();

        // Query database directly to assert persistence invariants
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        var persistedEvent = await db.Events
            .FirstOrDefaultAsync(e => e.Id == body!.EventId);

        persistedEvent.Should().NotBeNull();
        persistedEvent!.TenantId.Should().Be(Guid.Parse(RelayApiFactory.TenantAId));
        persistedEvent.EventType.Should().Be("payment.captured");
        persistedEvent.IdempotencyKey.Should().Be(idempotencyKey);
        persistedEvent.Status.Should().Be(EventStatus.Accepted);

        var persistedOutbox = await db.OutboxMessages
            .FirstOrDefaultAsync(o => o.EventId == body!.EventId);

        persistedOutbox.Should().NotBeNull();
        persistedOutbox!.TenantId.Should().Be(Guid.Parse(RelayApiFactory.TenantAId));
        persistedOutbox.MessageType.Should().Be("payment.captured");
        persistedOutbox.Status.Should().Be(OutboxStatus.Pending);
        persistedOutbox.ClaimCount.Should().Be(0);
        persistedOutbox.PublishAttemptCount.Should().Be(0);
        persistedOutbox.PublishedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Scenario05_DuplicateIdempotencyKey_ReturnsOriginalEventId()
    {
        // Arrange
        var idempotencyKey = $"idem_dup_{Guid.NewGuid()}";
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-DUP", "sku": "ABC"}""").RootElement);

        // 1. First submission
        using var request1 = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        request1.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        request1.Headers.Add("Idempotency-Key", idempotencyKey);

        var response1 = await _client.SendAsync(request1);
        response1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body1 = await response1.Content.ReadFromJsonAsync<IngestEventResponse>();

        // 2. Duplicate submission with identical content and same key
        using var request2 = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        request2.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        request2.Headers.Add("Idempotency-Key", idempotencyKey);

        var response2 = await _client.SendAsync(request2);

        // Assert
        response2.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body2 = await response2.Content.ReadFromJsonAsync<IngestEventResponse>();

        body2!.EventId.Should().Be(body1!.EventId);

        // Verify only ONE event and ONE outbox message exist in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        var eventCount = await db.Events.CountAsync(e => e.IdempotencyKey == idempotencyKey);
        var outboxCount = await db.OutboxMessages.CountAsync(o => o.EventId == body1.EventId);

        eventCount.Should().Be(1);
        outboxCount.Should().Be(1);
    }

    [Fact]
    public async Task Scenario06_ReusingIdempotencyKeyWithDifferentContent_Returns409Conflict()
    {
        // Arrange
        var idempotencyKey = $"idem_conflict_{Guid.NewGuid()}";
        var requestOriginal = new IngestEventRequest("order.created", JsonDocument.Parse("""{"amount": 100}""").RootElement);
        var requestConflicting = new IngestEventRequest("order.created", JsonDocument.Parse("""{"amount": 200}""").RootElement);

        // 1. Initial request
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(requestOriginal)
        };
        req1.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        req1.Headers.Add("Idempotency-Key", idempotencyKey);

        var resp1 = await _client.SendAsync(req1);
        resp1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body1 = await resp1.Content.ReadFromJsonAsync<IngestEventResponse>();

        // 2. Conflicting request with same key but different payload
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(requestConflicting)
        };
        req2.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        req2.Headers.Add("Idempotency-Key", idempotencyKey);

        var resp2 = await _client.SendAsync(req2);

        // Assert
        resp2.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Be("Idempotency Conflict");
        problem.GetProperty("existingEventId").GetString().Should().Be(body1!.EventId.ToString());
    }

    [Fact]
    public async Task Scenario07_ConcurrentDuplicateRequests_OnlyOneEventPersisted()
    {
        // Arrange
        var idempotencyKey = $"idem_race_{Guid.NewGuid()}";
        var request = new IngestEventRequest("order.created", JsonDocument.Parse("""{"orderId": "ORD-RACE", "parallel": true}""").RootElement);

        const int concurrency = 5;
        var tasks = new Task<HttpResponseMessage>[concurrency];

        // Act: Fire 5 identical requests concurrently
        for (var i = 0; i < concurrency; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
            {
                Content = JsonContent.Create(request)
            };
            req.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
            req.Headers.Add("Idempotency-Key", idempotencyKey);

            tasks[i] = _client.SendAsync(req);
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        var eventIds = new List<Guid>();
        foreach (var resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var body = await resp.Content.ReadFromJsonAsync<IngestEventResponse>();
            eventIds.Add(body!.EventId);
        }

        // All 5 requests must agree on the exact same Event ID
        eventIds.Distinct().Should().ContainSingle();

        // Database must contain exactly 1 event and 1 outbox message
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        var eventCount = await db.Events.CountAsync(e => e.IdempotencyKey == idempotencyKey);
        eventCount.Should().Be(1);
    }

    [Fact]
    public async Task Scenario08_TenantCannotRetrieveAnotherTenantsEvent_Returns404NotFound()
    {
        // Arrange: Tenant A creates an event
        var idempotencyKey = $"idem_tenant_iso_{Guid.NewGuid()}";
        var request = new IngestEventRequest("secret.tenant.event", JsonDocument.Parse("""{"confidential": true}""").RootElement);

        using var reqA = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(request)
        };
        reqA.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        reqA.Headers.Add("Idempotency-Key", idempotencyKey);

        var respA = await _client.SendAsync(reqA);
        respA.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var bodyA = await respA.Content.ReadFromJsonAsync<IngestEventResponse>();
        var tenantAEventId = bodyA!.EventId;

        // Act: Tenant B attempts to fetch Tenant A's event
        using var reqB = new HttpRequestMessage(HttpMethod.Get, $"/v1/events/{tenantAEventId}");
        reqB.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyB);

        var respB = await _client.SendAsync(reqB);

        // Assert: Strictly returns 404 Not Found to prevent resource enumeration
        respB.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scenario09_EventListingNeverLeaksDataBetweenTenants()
    {
        // Arrange
        // Tenant A creates 2 events
        for (var i = 0; i < 2; i++)
        {
            using var reqA = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
            {
                Content = JsonContent.Create(new IngestEventRequest($"tenant.a.evt.{i}", JsonDocument.Parse("{}").RootElement))
            };
            reqA.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
            reqA.Headers.Add("Idempotency-Key", $"idem_a_{i}_{Guid.NewGuid()}");
            await _client.SendAsync(reqA);
        }

        // Tenant B creates 1 event
        using var reqB = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(new IngestEventRequest("tenant.b.unique.event", JsonDocument.Parse("{}").RootElement))
        };
        reqB.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyB);
        reqB.Headers.Add("Idempotency-Key", $"idem_b_{Guid.NewGuid()}");
        await _client.SendAsync(reqB);

        // Act: Tenant B requests event list
        using var listReq = new HttpRequestMessage(HttpMethod.Get, "/v1/events?take=50");
        listReq.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyB);

        var listResp = await _client.SendAsync(listReq);

        // Assert: Tenant B sees ONLY Tenant B's events
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var paged = await listResp.Content.ReadFromJsonAsync<PagedResult<EventDto>>();
        paged.Should().NotBeNull();

        paged!.Items.Should().OnlyContain(e => e.TenantId == Guid.Parse(RelayApiFactory.TenantBId));
        paged.Items.Should().NotContain(e => e.TenantId == Guid.Parse(RelayApiFactory.TenantAId));
    }

    [Fact]
    public async Task Scenario11_InvalidRequests_ReturnAppropriate4xxStatus()
    {
        // 1. Missing Idempotency-Key header -> 400
        using var reqNoIdem = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(new IngestEventRequest("order.created", JsonDocument.Parse("{}").RootElement))
        };
        reqNoIdem.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        var respNoIdem = await _client.SendAsync(reqNoIdem);
        respNoIdem.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 2. Missing EventType -> 400
        using var reqNoType = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(new IngestEventRequest(null, JsonDocument.Parse("{}").RootElement))
        };
        reqNoType.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        reqNoType.Headers.Add("Idempotency-Key", "idem_notype");
        var respNoType = await _client.SendAsync(reqNoType);
        respNoType.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 3. Payload exceeding 32 KB -> 413
        var hugeData = new string('Z', 35 * 1024);
        using var reqHuge = new HttpRequestMessage(HttpMethod.Post, "/v1/events")
        {
            Content = JsonContent.Create(new IngestEventRequest("large.event", JsonDocument.Parse($$"""{"data":"{{hugeData}}"}""").RootElement))
        };
        reqHuge.Headers.Add("X-Api-Key", RelayApiFactory.ApiKeyA);
        reqHuge.Headers.Add("Idempotency-Key", "idem_huge");
        var respHuge = await _client.SendAsync(reqHuge);
        respHuge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }
}
