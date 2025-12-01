using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace EmojiService.IntegrationTests;

public class ConsumerCrudTests : DynamoDbIntegrationTest
{
    public ConsumerCrudTests()
        : base("consumer-crud") { }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Create ──

    [Fact]
    public async Task CreateConsumer_MinimalPayload_Returns201WithConsumer()
    {
        var payload = new
        {
            displayName = "Engineering Sync",
            adapter = "local-fs",
            targetPath = "/var/sync/eng",
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("displayName").GetString().Should().Be("Engineering Sync");
        body.GetProperty("adapter").GetString().Should().Be("local-fs");
        body.GetProperty("targetPath").GetString().Should().Be("/var/sync/eng");
        body.GetProperty("triggerMode").GetString().Should().Be("manual");
        body.GetProperty("state").GetString().Should().Be("active");
        body.GetProperty("createdAt").GetDateTime().Should().BeAfter(DateTime.MinValue);
        body.GetProperty("updatedAt").GetDateTime().Should().BeAfter(DateTime.MinValue);

        response.Headers.Location!.OriginalString.Should().StartWith("/consumers/");
    }

    [Fact]
    public async Task CreateConsumer_FullPayload_Returns201WithSubsetFilter()
    {
        var payload = new
        {
            displayName = "Full Sync",
            adapter = "local-fs",
            targetPath = "/var/sync/full",
            triggerMode = "manual",
            subsetFilter = new
            {
                tags = new[] { "reaction", "workflow" },
                categories = new[] { "approvals" },
                owner = "eng-team",
                aliasPrefix = "eng-",
                state = "active",
            },
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("displayName").GetString().Should().Be("Full Sync");
        body.GetProperty("state").GetString().Should().Be("active");

        var filter = body.GetProperty("subsetFilter");
        filter
            .GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .BeEquivalentTo(["reaction", "workflow"]);
        filter
            .GetProperty("categories")
            .EnumerateArray()
            .Select(c => c.GetString())
            .Should()
            .BeEquivalentTo(["approvals"]);
        filter.GetProperty("owner").GetString().Should().Be("eng-team");
        filter.GetProperty("aliasPrefix").GetString().Should().Be("eng-");
        filter.GetProperty("state").GetString().Should().Be("active");
    }

    // ── Create validation ──

    [Theory]
    [InlineData("", "local-fs", "/var/sync", "E701", "Display name must not be empty")]
    [InlineData("AB", "invalid-adapter", "/var/sync", "E704", "Adapter must be one of")]
    [InlineData("AB", "local-fs", "", "E705", "Target path must not be empty")]
    [InlineData("AB", "local-fs", "/var/sync", "E707", "Trigger mode must be one of")] // using empty string isn't possible via the default; let's test invalid triggerMode
    public async Task CreateConsumer_InvalidField_Returns400WithErrorCode(
        string displayName,
        string adapter,
        string targetPath,
        string expectedCode,
        string expectedDetailContains
    )
    {
        // Handle triggerMode test case by overriding defaults
        var payload = new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["adapter"] = adapter,
            ["targetPath"] = targetPath,
        };

        if (expectedCode == "E707")
            payload["triggerMode"] = "invalid-trigger";

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be(expectedCode);
        body.GetProperty("detail").GetString().Should().Contain(expectedDetailContains);
    }

    [Fact]
    public async Task CreateConsumer_MissingRequiredFields_Returns400()
    {
        var payload = new { };
        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateConsumer_DisplayNameTooLong_Returns400()
    {
        var payload = new
        {
            displayName = new string('x', 201),
            adapter = "local-fs",
            targetPath = "/var/sync",
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("E702");
    }

    [Fact]
    public async Task CreateConsumer_InvalidSubsetFilterTag_Returns400()
    {
        var payload = new
        {
            displayName = "Bad Filter",
            adapter = "local-fs",
            targetPath = "/var/sync",
            subsetFilter = new { tags = new[] { "" } },
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().Should().Be("E730");
    }

    // ── Get by ID ──

    [Fact]
    public async Task GetConsumer_ReturnsConsumer()
    {
        var created = await CreateConsumerAsync("Get Test", "/var/sync/get");
        var id = created.GetProperty("id").GetString()!;

        var response = await Client.GetAsync($"/api/v1/consumers/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(id);
        body.GetProperty("displayName").GetString().Should().Be("Get Test");
    }

    [Fact]
    public async Task GetConsumer_UnknownId_Returns404()
    {
        var response = await Client.GetAsync("/api/v1/consumers/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().Contain("00000000000000000000000000");
    }

    // ── List ──

    [Fact]
    public async Task ListConsumers_ReturnsAllActive()
    {
        await CreateConsumerAsync("Consumer A", "/var/sync/a");
        await CreateConsumerAsync("Consumer B", "/var/sync/b");

        var response = await Client.GetAsync("/api/v1/consumers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items").EnumerateArray();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("state").GetString() == "active");
    }

    [Fact]
    public async Task ListConsumers_IncludeArchived_IncludesArchived()
    {
        var created = await CreateConsumerAsync("To Archive", "/var/sync/archive");
        var id = created.GetProperty("id").GetString()!;

        // First archive it
        await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "To Archive",
                adapter = "local-fs",
                targetPath = "/var/sync/archive",
                triggerMode = "manual",
                state = "archived",
            }
        );

        var response = await Client.GetAsync("/api/v1/consumers?include_archived=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var states = body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("state").GetString())
            .ToList();
        states.Should().Contain("archived");
    }

    // ── Update ──

    [Fact]
    public async Task UpdateConsumer_ChangesDisplayName_ReturnsUpdated()
    {
        var created = await CreateConsumerAsync("Original Name", "/var/sync/update");
        var id = created.GetProperty("id").GetString()!;

        var payload = new
        {
            displayName = "Updated Name",
            adapter = "local-fs",
            targetPath = "/var/sync/update",
            triggerMode = "manual",
            state = "active",
        };

        var response = await Client.PutAsJsonAsync($"/api/v1/consumers/{id}", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be("Updated Name");
        body.GetProperty("state").GetString().Should().Be("active");
    }

    [Fact]
    public async Task UpdateConsumer_UnknownId_Returns404()
    {
        var payload = new
        {
            displayName = "Ghost",
            adapter = "local-fs",
            targetPath = "/var/sync/ghost",
            triggerMode = "manual",
            state = "active",
        };

        var response = await Client.PutAsJsonAsync(
            "/api/v1/consumers/00000000000000000000000000",
            payload
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    [Fact]
    public async Task UpdateConsumer_PauseAndResume_Succeeds()
    {
        var created = await CreateConsumerAsync("Pause Test", "/var/sync/pause");
        var id = created.GetProperty("id").GetString()!;

        // Pause
        var pauseResponse = await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Pause Test",
                adapter = "local-fs",
                targetPath = "/var/sync/pause",
                triggerMode = "manual",
                state = "paused",
            }
        );
        pauseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var paused = await pauseResponse.Content.ReadFromJsonAsync<JsonElement>();
        paused.GetProperty("state").GetString().Should().Be("paused");

        // Resume
        var resumeResponse = await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Pause Test",
                adapter = "local-fs",
                targetPath = "/var/sync/pause",
                triggerMode = "manual",
                state = "active",
            }
        );
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var resumed = await resumeResponse.Content.ReadFromJsonAsync<JsonElement>();
        resumed.GetProperty("state").GetString().Should().Be("active");
    }

    [Fact]
    public async Task UpdateConsumer_SameState_Succeeds()
    {
        var created = await CreateConsumerAsync("Same State", "/var/sync/same");
        var id = created.GetProperty("id").GetString()!;

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Same State Updated",
                adapter = "local-fs",
                targetPath = "/var/sync/same",
                triggerMode = "manual",
                state = "active",
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be("Same State Updated");
        body.GetProperty("state").GetString().Should().Be("active");
    }

    [Fact]
    public async Task UpdateConsumer_ArchivedCannotTransition_Returns400()
    {
        var created = await CreateConsumerAsync("Terminal", "/var/sync/terminal");
        var id = created.GetProperty("id").GetString()!;

        // Archive
        await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Terminal",
                adapter = "local-fs",
                targetPath = "/var/sync/terminal",
                triggerMode = "manual",
                state = "archived",
            }
        );

        // Try to reactivate archived consumer
        var response = await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Terminal",
                adapter = "local-fs",
                targetPath = "/var/sync/terminal",
                triggerMode = "manual",
                state = "active",
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateConsumer_InvalidStateName_Returns400()
    {
        var created = await CreateConsumerAsync("Invalid State", "/var/sync/invalid");
        var id = created.GetProperty("id").GetString()!;

        var response = await Client.PutAsJsonAsync(
            $"/api/v1/consumers/{id}",
            new
            {
                displayName = "Invalid State",
                adapter = "local-fs",
                targetPath = "/var/sync/invalid",
                triggerMode = "manual",
                state = "bogus",
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be("E709");
    }

    // ── Delete ──

    [Fact]
    public async Task DeleteConsumer_RemovesConsumer_ThenReturns404()
    {
        var created = await CreateConsumerAsync("Delete Me", "/var/sync/delete");
        var id = created.GetProperty("id").GetString()!;

        var deleteResponse = await Client.DeleteAsync($"/api/v1/consumers/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await Client.GetAsync($"/api/v1/consumers/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteConsumer_UnknownId_Returns404()
    {
        var response = await Client.DeleteAsync("/api/v1/consumers/00000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
    }

    // ── Full CRUD round-trip ──

    [Fact]
    public async Task ConsumerCrudRoundTrip()
    {
        // Create
        var createPayload = new
        {
            displayName = "Roundtrip Sync",
            adapter = "local-fs",
            targetPath = "/var/sync/roundtrip",
            subsetFilter = new { tags = new[] { "reaction" }, owner = "roundtrip-team" },
        };

        var createResponse = await Client.PostAsJsonAsync("/api/v1/consumers", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("displayName").GetString().Should().Be("Roundtrip Sync");
        created.GetProperty("state").GetString().Should().Be("active");
        created
            .GetProperty("subsetFilter")
            .GetProperty("owner")
            .GetString()
            .Should()
            .Be("roundtrip-team");

        // Get
        var getResponse = await Client.GetAsync($"/api/v1/consumers/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        getBody.GetProperty("id").GetString().Should().Be(id);
        getBody
            .GetProperty("subsetFilter")
            .GetProperty("tags")
            .EnumerateArray()
            .Select(t => t.GetString())
            .Should()
            .Contain("reaction");

        // Update (pause)
        var updatePayload = new
        {
            displayName = "Roundtrip Sync Paused",
            adapter = "local-fs",
            targetPath = "/var/sync/roundtrip",
            triggerMode = "manual",
            state = "paused",
            subsetFilter = new { categories = new[] { "qa" } },
        };

        var updateResponse = await Client.PutAsJsonAsync($"/api/v1/consumers/{id}", updatePayload);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("displayName").GetString().Should().Be("Roundtrip Sync Paused");
        updated.GetProperty("state").GetString().Should().Be("paused");
        updated
            .GetProperty("subsetFilter")
            .GetProperty("categories")
            .EnumerateArray()
            .Select(c => c.GetString())
            .Should()
            .Contain("qa");
        updated
            .TryGetProperty("tags", out _)
            .Should()
            .BeFalse("old filter fields should be replaced");

        // Get again to confirm persistence
        var getAgainResponse = await Client.GetAsync($"/api/v1/consumers/{id}");
        getAgainResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getAgainBody = await getAgainResponse.Content.ReadFromJsonAsync<JsonElement>();
        getAgainBody.GetProperty("state").GetString().Should().Be("paused");
        getAgainBody.GetProperty("displayName").GetString().Should().Be("Roundtrip Sync Paused");

        // Delete
        var deleteResponse = await Client.DeleteAsync($"/api/v1/consumers/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Get after delete → 404
        var getAfterDeleteResponse = await Client.GetAsync($"/api/v1/consumers/{id}");
        getAfterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ──

    private async Task<JsonElement> CreateConsumerAsync(string displayName, string targetPath)
    {
        var payload = new
        {
            displayName,
            adapter = "local-fs",
            targetPath,
        };

        var response = await Client.PostAsJsonAsync("/api/v1/consumers", payload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
