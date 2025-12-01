# ADR-0010: Consumer registration schema

**Status:** Accepted

## Context

ADR-0009 defined the propagation model — push-triggered, manifest-driven, with adapters that read assets locally and apply them to target systems. It sketched a preliminary consumer record shape but left the final schema, subset filter language, and storage layout to a dedicated ADR.

Before building consumer registration endpoints or sync flows, we must lock the v1 consumer record shape. A consumer record describes **who** receives emoji data, **what subset**, **how** it is delivered, and on **what schedule** (even if v1 only supports manual triggers). This ADR also defines the subset filter language — reusing the search filter shape from the search endpoint — so that both query-time filtering and consumer-side filtering use the same vocabulary.

## Decision

### 1. Consumer record shape

```csharp
public sealed record Consumer
{
    public required string Id { get; init; }               // ULID, stable identity
    public required string DisplayName { get; init; }       // human-readable label
    public required string Adapter { get; init; }           // "local-fs" in v1
    public required string TargetPath { get; init; }        // adapter-specific output location
    public ConsumerSubsetFilter? SubsetFilter { get; init; } // null = all resolvable emoji
    public required string TriggerMode { get; init; }       // "manual" in v1
    public required string State { get; init; }             // "active" | "paused" | "archived"
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}
```

| Field          | Type           | Required | v1 values                                   | Purpose                                                                                                                           |
|----------------|----------------|----------|---------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| `Id`           | string (ULID)  | Yes      | Generated on create                         | Stable, URL-safe consumer identity                                                                                                |
| `DisplayName`  | string         | Yes      | Free text, ≤ 200 chars                      | Human-readable label shown in admin UI and logs                                                                                   |
| `Adapter`      | string         | Yes      | `"local-fs"`                                | Which `IPropagationAdapter` to resolve from DI                                                                                    |
| `TargetPath`   | string         | Yes      | Filesystem path, e.g. `/var/sync/eng-team/` | Adapter-specific output location. For `local-fs`, the directory where the manifest JSON and asset copies are written              |
| `SubsetFilter` | object \| null | No       | `null` (all resolvable) or a filter object  | Which emoji this consumer receives. `null` means no filtering — all emoji in resolvable states are included                       |
| `TriggerMode`  | string         | Yes      | `"manual"`                                  | How sync is invoked. `"manual"` means only `POST /consumers/{id}/sync`. `"on_change"` and `"scheduled"` are reserved for post-MVP |
| `State`        | string         | Yes      | `"active"`, `"paused"`, `"archived"`        | Lifecycle state of the consumer registration itself                                                                               |
| `CreatedAt`    | DateTime (UTC) | Yes      | Set on create                               | Immutable creation timestamp                                                                                                      |
| `UpdatedAt`    | DateTime (UTC) | Yes      | Set on create, updated on mutation          | Last modification timestamp                                                                                                       |

**Runtime sync state** (last sync status, last manifest ID, last sync timestamp) is not part of the consumer registration record. That state belongs to the sync flow and will be stored in either the consumer record (added as optional fields in the sync ADR) or in separate sync-history items. This ADR defines only the registration schema — the static configuration that describes a consumer.

**Adapter-specific configuration beyond `TargetPath`** (e.g., Slack workspace tokens, Discord guild IDs) is deferred. When additional adapters are added post-MVP, configuration will be added as an optional `Config` dictionary on the consumer record. For v1, `TargetPath` is the only configuration needed.

### 2. Subset filter language

The subset filter reuses the search filter shape from the `GET /emoji` endpoint. A consumer's `SubsetFilter` determines which emoji are included in its sync manifest. Semantics: an emoji is included if it matches **all** specified filter criteria. Within a multi-value dimension (tags, categories), an emoji matches if it has **any** of the listed values. A null or empty dimension means "match all."

```csharp
public sealed record ConsumerSubsetFilter
{
    public IReadOnlyList<string>? Tags { get; init; }       // ANY match (OR within dimension)
    public IReadOnlyList<string>? Categories { get; init; }  // ANY match (OR within dimension)
    public string? Owner { get; init; }                      // exact match
    public string? AliasPrefix { get; init; }                // prefix match on any alias (primary or secondary)
    public string? State { get; init; }                      // exact match on lifecycle state
}
```

| Dimension     | Type               | Match semantics                                          | Example                                                                                                            |
|---------------|--------------------|----------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------|
| `Tags`        | `string[]` \| null | Emoji has ANY of the listed tags                         | `["reaction", "workflow"]` — matches emoji tagged "reaction" OR "workflow"                                         |
| `Categories`  | `string[]` \| null | Emoji has ANY of the listed categories                   | `["approvals"]` — matches emoji in the "approvals" category                                                        |
| `Owner`       | `string` \| null   | Emoji owner equals this value (case-insensitive)         | `"eng-team"` — only emoji owned by eng-team                                                                        |
| `AliasPrefix` | `string` \| null   | Any alias (primary or secondary) starts with this prefix | `"eng-"` — matches emoji with aliases like `eng-approved`, `eng-rejected`                                          |
| `State`       | `string` \| null   | Emoji lifecycle state equals this value                  | `"active"` — only active emoji. Default when the filter is null: `"active"` and `"deprecated"` (resolvable states) |

**Filter evaluation algorithm:**

```
include(emoji):
  if filter is null → true (all resolvable emoji)
  if filter.Tags is not null and emoji.Tags ∩ filter.Tags is empty → false
  if filter.Categories is not null and emoji.Categories ∩ filter.Categories is empty → false
  if filter.Owner is not null and emoji.Owner ≠ filter.Owner (case-insensitive) → false
  if filter.AliasPrefix is not null and no alias starts with filter.AliasPrefix → false
  if filter.State is not null and emoji.State ≠ filter.State → false
  return true
```

**Default behavior when `SubsetFilter` is null:** The sync manifest includes all emoji whose lifecycle state is `active` or `deprecated` (the resolvable states defined in `LifecycleState.Resolvable`). This matches the search endpoint's default and VISION.md's resolvability rule.

**Why reuse the search filter shape:**
- Operators already know the search dimensions. Consumer filters use the same vocabulary.
- The filter object can be passed directly to `IEmojiIndex.Search()` — no translation layer needed.
- Adding a new filter dimension to the search endpoint automatically makes it available for consumer filters.

### 3. Consumer state machine

The consumer registration has its own lifecycle, independent of the emoji lifecycle:

```
                  ┌──────────┐
         ┌───────▷│  active  │◁───────┐
         │        └─────┬────┘        │
         │              │             │
         │              ▼             │
         │        ┌──────────┐        │
         │        │  paused  │────────┘
         │        └─────┬────┘
         │              │
         │              ▼
         │        ┌──────────┐
         └────────│ archived │ (terminal)
                  └──────────┘
```

| State      | Meaning                                | Behavior                                                                                                                  |
|------------|----------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `active`   | Consumer is live and can receive syncs | Sync endpoints accept requests. Consumer appears in listing.                                                              |
| `paused`   | Consumer is temporarily suspended      | Sync endpoints return 409 Conflict. Consumer appears in listing with paused flag. Can be reactivated.                     |
| `archived` | Consumer is permanently retired        | Sync endpoints return 404. Consumer is excluded from default listing. Preserved for audit history. Cannot be reactivated. |

**Transitions:**

| From       | To         | Allowed?      |
|------------|------------|---------------|
| `active`   | `paused`   | Yes           |
| `active`   | `archived` | Yes           |
| `paused`   | `active`   | Yes           |
| `paused`   | `archived` | Yes           |
| `archived` | any        | No (terminal) |

**Why a state machine for consumers:**
- Pausing a consumer is the primary mechanism for stopping propagation without deleting configuration. The operator can pause a consumer, fix the issue, and resume — all without losing the consumer's filter, target path, or sync history.
- Archiving preserves the consumer record for audit purposes while excluding it from active operations. This matters when a downstream system is decommissioned and the operator needs to know what was being synced where.
- Three states (active, paused, archived) is the simplest model that covers the operational lifecycle. Additional states (e.g., `error` for consumers that have failed syncs) can be derived from sync history rather than embedded in the consumer state.

### 4. DynamoDB storage layout

Following the single-table key schema from ADR-0006:

| Attribute    | Value           |
|--------------|-----------------|
| `PK`         | `CONSUMER#{id}` |
| `SK`         | `META`          |
| `EntityType` | `Consumer`      |
| `GSI1PK`     | *(not set)*     |
| `GSI1SK`     | *(not set)*     |

Individual attributes for the consumer fields:

| Attribute      | Type | Value                                             |
|----------------|------|---------------------------------------------------|
| `PK`           | S    | `CONSUMER#{id}`                                   |
| `SK`           | S    | `META`                                            |
| `EntityType`   | S    | `Consumer`                                        |
| `DisplayName`  | S    | Human-readable label                              |
| `Adapter`      | S    | `"local-fs"`                                      |
| `TargetPath`   | S    | Filesystem path                                   |
| `SubsetFilter` | S    | JSON-serialized `ConsumerSubsetFilter`, or `null` |
| `TriggerMode`  | S    | `"manual"`                                        |
| `State`        | S    | `"active"` \| `"paused"` \| `"archived"`          |
| `CreatedAt`    | S    | ISO 8601 UTC timestamp                            |
| `UpdatedAt`    | S    | ISO 8601 UTC timestamp                            |

The `SubsetFilter` is stored as a JSON string because it is an optional nested object with list-valued dimensions. Serializing it as JSON avoids the complexity of DynamoDB list/map attributes for an optional, sparsely-populated structure. Null filters are stored as an absent attribute (not a `"null"` string).

**Why individual attributes rather than a `Data` JSON blob:** The consumer record follows the same pattern as the Emoji entity (ADR-0006) — its fields are well-known, bounded in number, and individually useful for debugging in the AWS Console. The filter object is the only nested structure and is stored as JSON within its own attribute.

### 5. v1 access patterns

| Pattern            | Operation                                               | Key                           |
|--------------------|---------------------------------------------------------|-------------------------------|
| Get consumer by ID | `GetItem`                                               | PK=`CONSUMER#{id}`, SK=`META` |
| List all consumers | `Scan` with `FilterExpression: EntityType = "Consumer"` | Table scan                    |

For MVP, listing all consumers uses a table scan filtered by `EntityType`. Consumer count is expected to be small (single digits to low tens), so a scan is acceptable. A dedicated GSI partition (`GSI1PK=CONSUMER`) can be added later if consumer count grows, but that infrastructure is not justified for v1.

## Consequences

**Positive:**
- Consumer records have a clear, documented shape. API endpoints and repository code can be written against this spec without ambiguity.
- The subset filter reuses the search vocabulary exactly. No new filter language to learn, implement, or test.
- The consumer state machine (active → paused → archived) is simple but covers the full operational lifecycle.
- Individual DynamoDB attributes make consumer records inspectable in the AWS Console.
- Storage follows the existing key schema conventions (`PK=CONSUMER#{id}`, `SK=META`). No schema migration needed — just add a new type prefix.

**Negative:**
- Listing all consumers uses a table scan. Acceptable at MVP scale (single-digit consumers) but may need a GSI if consumer count grows.
- Adapter-specific configuration beyond `TargetPath` is deferred. When Slack or Discord adapters are added post-MVP, the consumer record will need a `Config` dictionary (backwards-compatible addition — new optional field, existing records unaffected).
- The `SubsetFilter` is stored as a JSON string, which means DynamoDB cannot filter on individual filter dimensions at the database level. All filtering is done in the application layer against the in-memory index — consistent with ADR-0007's approach for emoji search.

## Alternatives Considered

| Alternative                                                         | Why rejected                                                                                                                                                                                                                                                                                                                            |
|---------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Store consumer as a `Data` JSON blob (like Alias/Audit)             | Consumer fields are well-known and bounded. Individual attributes are easier to inspect in the AWS Console and follow the Emoji entity pattern.                                                                                                                                                                                         |
| Use the `Config` dictionary (from ADR-0009 sketch) for `TargetPath` | `TargetPath` is the only adapter config in v1 and is required for every consumer. Promoting it to a first-class field makes the schema self-documenting and enables validation. The `Config` dictionary is deferred to post-MVP when additional adapters need platform-specific settings.                                               |
| Embed runtime sync state in the consumer record                     | Sync state (last sync result, timestamps) belongs to the sync flow, not the registration schema. Including it here would couple the consumer model to sync implementation details. Sync state will be added when the sync endpoints are built — either as new optional fields on the consumer record or as separate sync-history items. |
| Use a GSI for listing consumers                                     | A scan with `EntityType` filter is sufficient for v1 consumer counts. Adding a GSI costs ~$0.25 per million writes with no benefit when there are fewer than ~100 consumers.                                                                                                                                                            |
| Include `OnChange` and `Schedule` fields now                        | These are post-MVP trigger modes. Including them now would invite partial implementation. They are documented as reserved values for `TriggerMode` and will be added to the record shape when their corresponding trigger infrastructure is built.                                                                                      |
