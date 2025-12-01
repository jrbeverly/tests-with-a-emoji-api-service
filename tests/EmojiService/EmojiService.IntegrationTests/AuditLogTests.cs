using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmojiService.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EmojiService.IntegrationTests;

public class AuditLogTests : DynamoDbIntegrationTest
{
    public AuditLogTests()
        : base("audit") { }

    private static string NewUid() => Ulid.NewUlid().ToString().ToLowerInvariant();

    [Fact]
    public async Task CreateEmoji_WritesAuditEvent()
    {
        var payload = new
        {
            primaryAlias = "audited-emoji",
            displayName = "Audited Emoji",
            contentType = "image/png",
            assetReference = "assets/audited.png",
            description = "An emoji with audit trail",
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var uid = created.GetProperty("uid").GetString()!;

        var auditResponse = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        auditResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = auditBody.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1);
        items[0].GetProperty("action").GetString().Should().Be("emoji.upload");
        items[0].GetProperty("actor").GetString().Should().Be("system");
        items[0].GetProperty("subjectUid").GetString().Should().Be(uid);
        items[0].GetProperty("after").ValueKind.Should().Be(JsonValueKind.String);
        items[0].GetProperty("eventId").GetString().Should().NotBeNullOrEmpty();
        items[0].GetProperty("occurredAt").GetString().Should().NotBeNullOrEmpty();
        auditBody.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetEmojiAudit_ReturnsEventsNewestFirst()
    {
        var auditLog = GetAuditLog();
        var uid = NewUid();

        var olderEventId = Ulid.NewUlid().ToString().ToLowerInvariant();
        await Task.Delay(10);
        var newerEventId = Ulid.NewUlid().ToString().ToLowerInvariant();
        var baseTime = DateTime.UtcNow;

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = olderEventId,
                Actor = "admin",
                Action = "emoji.upload",
                SubjectUid = uid,
                After = "{}",
                OccurredAt = baseTime.AddMinutes(-2),
            }
        );

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = newerEventId,
                Actor = "admin",
                Action = "emoji.approve",
                SubjectUid = uid,
                Before = "{}",
                After = "{}",
                OccurredAt = baseTime,
            }
        );

        var response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items[0].GetProperty("action").GetString().Should().Be("emoji.approve");
        items[1].GetProperty("action").GetString().Should().Be("emoji.upload");
    }

    [Fact]
    public async Task GetGlobalAudit_ReturnsEventsAcrossAllEmoji()
    {
        var auditLog = GetAuditLog();
        var baseTime = DateTime.UtcNow;
        var uid1 = NewUid();
        var uid2 = NewUid();

        var olderEventId = Ulid.NewUlid().ToString().ToLowerInvariant();
        await Task.Delay(10);
        var newerEventId = Ulid.NewUlid().ToString().ToLowerInvariant();

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = olderEventId,
                Actor = "admin",
                Action = "emoji.upload",
                SubjectUid = uid1,
                After = "{}",
                OccurredAt = baseTime.AddMinutes(-3),
            }
        );

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = newerEventId,
                Actor = "admin",
                Action = "emoji.upload",
                SubjectUid = uid2,
                After = "{}",
                OccurredAt = baseTime.AddMinutes(-1),
            }
        );

        var response = await Client.GetAsync("/api/v1/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(2);
        items[0].GetProperty("subjectUid").GetString().Should().Be(uid2);
        items[1].GetProperty("subjectUid").GetString().Should().Be(uid1);
    }

    [Fact]
    public async Task RemapAction_IsWrittenAndQueryable()
    {
        var auditLog = GetAuditLog();
        var uid = NewUid();

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = NewUid(),
                Actor = "admin",
                Action = "alias.remap",
                SubjectUid = uid,
                Before = "{\"alias\":\"old-name\"}",
                After = "{\"alias\":\"new-name\"}",
                OccurredAt = DateTime.UtcNow,
                Reason = "User requested remap",
            }
        );

        var response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1);
        items[0].GetProperty("action").GetString().Should().Be("alias.remap");
        items[0].GetProperty("reason").GetString().Should().Be("User requested remap");
    }

    [Fact]
    public async Task ProtectedOverrideAction_IsWrittenAndQueryable()
    {
        var auditLog = GetAuditLog();
        var uid = NewUid();

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = NewUid(),
                Actor = "admin",
                Action = "alias.unblock",
                SubjectUid = uid,
                Before = "{\"state\":\"blocked\"}",
                After = "{\"state\":\"active\"}",
                OccurredAt = DateTime.UtcNow,
                Reason = "Override block for migration",
            }
        );

        var response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1);
        items[0].GetProperty("action").GetString().Should().Be("alias.unblock");
        items[0].GetProperty("reason").GetString().Should().Be("Override block for migration");
    }

    [Fact]
    public async Task StateTransitionAction_IsWrittenAndQueryable()
    {
        var auditLog = GetAuditLog();
        var uid = NewUid();

        await auditLog.WriteAsync(
            new AuditEvent
            {
                EventId = NewUid(),
                Actor = "admin",
                Action = "emoji.deprecate",
                SubjectUid = uid,
                Before = "{\"state\":\"active\"}",
                After = "{\"state\":\"deprecated\"}",
                OccurredAt = DateTime.UtcNow,
                Reason = "Superseded by new version",
            }
        );

        var response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(1);
        items[0].GetProperty("action").GetString().Should().Be("emoji.deprecate");
        items[0].GetProperty("reason").GetString().Should().Be("Superseded by new version");
    }

    [Fact]
    public async Task GetEmojiAudit_Pagination_IsStable()
    {
        var auditLog = GetAuditLog();
        var uid = NewUid();
        var baseTime = DateTime.UtcNow;

        var eventIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(5);
            var eventId = Ulid.NewUlid().ToString().ToLowerInvariant();
            eventIds.Add(eventId);
            await auditLog.WriteAsync(
                new AuditEvent
                {
                    EventId = eventId,
                    Actor = "admin",
                    Action = "emoji.upload",
                    SubjectUid = uid,
                    OccurredAt = baseTime.AddMinutes(-i),
                }
            );
        }

        var page1Response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit?limit=2");
        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await page1Response.Content.ReadFromJsonAsync<JsonElement>();

        var page1Items = page1.GetProperty("items").EnumerateArray().ToList();
        page1Items.Should().HaveCount(2);
        page1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor = page1.GetProperty("cursor").GetString();
        cursor.Should().NotBeNull();

        var page2Response = await Client.GetAsync(
            $"/api/v1/emoji/{uid}/audit?limit=2&cursor={cursor}"
        );
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await page2Response.Content.ReadFromJsonAsync<JsonElement>();

        var page2Items = page2.GetProperty("items").EnumerateArray().ToList();
        page2Items.Should().HaveCount(2);
        page2.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor2 = page2.GetProperty("cursor").GetString();

        var page3Response = await Client.GetAsync(
            $"/api/v1/emoji/{uid}/audit?limit=2&cursor={cursor2}"
        );
        page3Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page3 = await page3Response.Content.ReadFromJsonAsync<JsonElement>();

        var page3Items = page3.GetProperty("items").EnumerateArray().ToList();
        page3Items.Should().HaveCount(1);
        page3.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        var allEventIds = page1Items
            .Concat(page2Items)
            .Concat(page3Items)
            .Select(e => e.GetProperty("eventId").GetString())
            .ToList();

        allEventIds.Should().HaveCount(5);
        allEventIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetGlobalAudit_Pagination_IsStable()
    {
        var auditLog = GetAuditLog();
        var baseTime = DateTime.UtcNow;

        var eventIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(5);
            var eventId = Ulid.NewUlid().ToString().ToLowerInvariant();
            eventIds.Add(eventId);
            await auditLog.WriteAsync(
                new AuditEvent
                {
                    EventId = eventId,
                    Actor = "admin",
                    Action = "emoji.upload",
                    SubjectUid = NewUid(),
                    OccurredAt = baseTime.AddMinutes(-i),
                }
            );
        }

        var page1Response = await Client.GetAsync("/api/v1/audit?limit=2");
        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await page1Response.Content.ReadFromJsonAsync<JsonElement>();

        var page1Items = page1.GetProperty("items").EnumerateArray().ToList();
        page1Items.Should().HaveCount(2);
        page1.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor = page1.GetProperty("cursor").GetString();
        cursor.Should().NotBeNull();

        var page2Response = await Client.GetAsync($"/api/v1/audit?limit=2&cursor={cursor}");
        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await page2Response.Content.ReadFromJsonAsync<JsonElement>();

        var page2Items = page2.GetProperty("items").EnumerateArray().ToList();
        page2Items.Should().HaveCount(2);
        page2.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        var cursor2 = page2.GetProperty("cursor").GetString();

        var page3Response = await Client.GetAsync($"/api/v1/audit?limit=2&cursor={cursor2}");
        page3Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page3 = await page3Response.Content.ReadFromJsonAsync<JsonElement>();

        var page3Items = page3.GetProperty("items").EnumerateArray().ToList();
        page3Items.Should().HaveCount(1);
        page3.GetProperty("hasMore").GetBoolean().Should().BeFalse();

        var allEventIds = page1Items
            .Concat(page2Items)
            .Concat(page3Items)
            .Select(e => e.GetProperty("eventId").GetString())
            .ToList();

        allEventIds.Should().HaveCount(5);
        allEventIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetEmojiAudit_InvalidUid_Returns400()
    {
        var response = await Client.GetAsync("/api/v1/emoji/invalid-uid/audit");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetEmojiAudit_InvalidLimit_Returns400()
    {
        var validUid = NewUid();
        var response = await Client.GetAsync($"/api/v1/emoji/{validUid}/audit?limit=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetGlobalAudit_InvalidLimit_Returns400()
    {
        var response = await Client.GetAsync("/api/v1/audit?limit=0");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetEmojiAudit_NoEvents_ReturnsEmpty()
    {
        var uid = NewUid();
        var response = await Client.GetAsync($"/api/v1/emoji/{uid}/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetGlobalAudit_ReturnsValidResponse()
    {
        var response = await Client.GetAsync("/api/v1/audit");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("items", out _).Should().BeTrue();
        body.TryGetProperty("hasMore", out _).Should().BeTrue();
    }

    private IAuditLog GetAuditLog()
    {
        return Factory.Services.GetRequiredService<IAuditLog>();
    }
}
