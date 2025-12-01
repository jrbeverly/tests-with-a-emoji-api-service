# EmojiService Tests

## Projects

| Project                         | Purpose                                              |
|---------------------------------|------------------------------------------------------|
| `EmojiService.Tests`            | Unit tests (domain logic, value objects, validation) |
| `EmojiService.IntegrationTests` | Integration tests (API + DynamoDB Local)             |

## Integration Test Harness

Integration tests use a reusable `DynamoDbIntegrationTest` base class that provides:

- **`WebApplicationFactory<Program>`** — hosts the API in-process.
- **DynamoDB Local** — isolated tables created and torn down per test class.
- **Per-class table prefix** — each test class supplies a unique prefix (e.g. `"sample"` → table name `emoji-it-sample`), ensuring tests never interfere with each other.
- **`IAsyncLifetime`** — tables are created during `InitializeAsync` and deleted during `DisposeAsync`.

### Writing an Integration Test

1. Inherit from `DynamoDbIntegrationTest`.
2. Pass a unique table prefix to the base constructor.
3. Use the `Client` property to call the API.

```csharp
public class MyIntegrationTests : DynamoDbIntegrationTest
{
    public MyIntegrationTests()
        : base("my-feature")  // isolated table name
    {
    }

    [Fact]
    public async Task MyTest()
    {
        var payload = new { primaryAlias = "test", displayName = "Test",
            contentType = "image/png", assetReference = "assets/test.png" };

        var response = await Client.PostAsJsonAsync("/api/v1/emoji", payload);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
```

### Prerequisites

DynamoDB Local must be running at `http://localhost:8000`. In the devcontainer it starts automatically. Verify with:

```bash
make smoke-dynamo
```

### Running Tests

```bash
# All tests (unit + integration)
dotnet test EmojiService.slnx

# Integration tests only
dotnet test tests/EmojiService/EmojiService.IntegrationTests/
```

### Isolation Model

- Each test class inheriting from `DynamoDbIntegrationTest` gets its own DynamoDB table.
- Tables use the naming pattern `emoji-it-{prefix}` where `prefix` is the value passed to the base constructor.
- Tables are deleted on test completion (best-effort cleanup).
- This is simpler and faster than per-test container restarts.
