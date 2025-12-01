# ADR-0009: Propagation model and sync triggers

**Status:** Accepted

## Context

VISION.md section 7 names synchronization and propagation as one of the most important capabilities: "The Emoji Service should not assume that every application will call it directly for every emoji lookup. Instead, it should be able to publish, push, export, or synchronize emoji data into other places."

The service already has (or will soon have) the core registry: upload, alias management, lifecycle transitions, batch apply, audit, and search. What remains is the propagation layer — the mechanism by which emoji data moves from the central registry to registered downstream consumers.

We must decide the propagation model, trigger mechanisms, adapter contract, failure semantics, and capability representation before implementing consumer registration or sync endpoints. These decisions shape Phase 4 (Bridge) of the MVP.

The solo-founder constraint applies: the model must be locally testable with DynamoDB Local and the local filesystem, zero-cost-when-idle, and simple enough to prove end-to-end without external platforms.

## Decision

### 1. Push-generates-manifest, adapter reads locally

The propagation model is **push-triggered, manifest-driven, with local asset access**:

```
┌──────────────────────┐     ┌──────────────────────────────┐     ┌───────────────────┐
│   Sync Trigger       │     │   IPropagationAdapter         │     │   Target          │
│   (manual / on-      │────▷│   SyncAsync(consumer,        │────▷│   (filesystem,    │
│    change / sched)   │     │   manifest)                   │     │    Slack, etc.)   │
└──────────────────────┘     └──────────┬───────────────────┘     └───────────────────┘
                                        │
                                        │ reads assets locally
                                        ▼
                               ┌──────────────────┐
                               │   IAssetStore    │
                               │   (local / S3)   │
                               └──────────────────┘
```

- **The service generates a manifest** — a snapshot of all emoji (or a filtered subset) that the consumer should have, including metadata and asset references.
- **The adapter consumes the manifest** — it receives the manifest and is responsible for applying it to the target system. The adapter reads asset bytes from `IAssetStore`, so the propagation pipeline never moves binary payloads through the service layer.
- **The adapter runs in-process** — adapters are called synchronously by the service. There is no external queue or worker. For MVP, this keeps the system locally testable and avoids infrastructure dependencies.

**Why not pure push (service uploads to target):**
That would couple the service to external platform APIs and break local testing. The adapter pattern isolates platform-specific logic behind a narrow interface.

**Why not pure pull (consumer polls the service):**
That would require every consumer to maintain its own polling infrastructure. Push-triggered (with the service initiating the sync) is simpler for the MVP and gives the service control over when propagation happens. Consumers that prefer polling can be built later — nothing in this ADR prevents a future consumer from calling `GET /emoji` to build its own manifest.

### 2. Manifest shape

A propagation manifest is a JSON document generated at sync time:

```jsonc
{
  "manifest_id": "01jabc123...",       // ULID, unique per sync run
  "consumer_id": "01jdef456...",       // consumer this manifest is for
  "generated_at": "2026-06-18T12:00:00Z",
  "emoji": [
    {
      "uid": "01jghi789...",
      "primary_alias": "approved",
      "secondary_aliases": ["ok", "approved-stamp"],
      "display_name": "Approved Stamp",
      "description": "A green approval stamp",
      "content_type": "image/png",
      "asset_reference": "01jghi789/original.png",
      "tags": ["reaction", "workflow"],
      "categories": ["approvals"],
      "owner": "eng-team",
      "state": "active",
      "created_at": "2026-06-01T12:00:00Z",
      "updated_at": "2026-06-15T08:30:00Z"
    }
  ],
  "filter": {                           // null = unfiltered (all emoji)
    "tags": null,
    "categories": null,
    "owner": null,
    "state": "active"                   // default: active and deprecated only
  }
}
```

Key design points:

- **Asset references, not asset bytes.** The manifest contains `asset_reference` strings that the adapter passes to `IAssetStore.GetAsync()`. This keeps the manifest small and avoids duplicating binary data in the propagation pipeline.
- **Filter support.** Each consumer can be configured with a filter (by tag, category, owner, state). The manifest only includes matching emoji. This enables team-specific or channel-specific propagation without duplicating the registry.
- **States default to `active` and `deprecated`.** Pending, disabled, and removed emoji are excluded by default. A consumer can request a broader filter, but the default matches VISION.md's resolvability rule.
- **`manifest_id` is a ULID.** Every sync run produces a unique manifest ID, recorded in the audit log and the consumer's sync history.

### 3. Trigger model: manual primary, on-change secondary, scheduled future

Three trigger mechanisms are defined, with manual as the v1 implementation:

| Trigger       | How invoked                            | MVP?     | Description                                                                                                                                                                                            |
|---------------|----------------------------------------|----------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Manual**    | `POST /consumers/{id}/sync`            | Yes      | Explicit API call. Returns sync result synchronously or as an accepted job reference. Full audit trail.                                                                                                |
| **On-change** | Hook fired after emoji CRUD operations | Post-MVP | After a create/update/delete/transition, the service checks which consumers are subscribed to the affected emoji and enqueues a sync. The consumer model includes an `on_change` flag that gates this. |
| **Scheduled** | Cron expression on consumer record     | Post-MVP | Consumer has an optional `schedule` field. A scheduled trigger (e.g., Lambda scheduled event, cron job) evaluates which consumers are due and invokes sync for each.                                   |

**Manual sync for MVP** because:
- It is trivially testable — send a POST, observe the result.
- It requires no background processing, no queues, and no scheduling infrastructure.
- It gives the operator full control over when propagation happens.
- It works identically in local dev, CI, and production.

**On-change as a documented extension point** because it is the natural next step: emoji mutations can fire a sync to subscribed consumers. The consumer record carries an `on_change: bool` field so the operator can opt consumers into automatic sync. Implementation requires a lightweight in-process hook (not a message queue) — the sync can be fire-and-forget or awaited depending on the consumer's `sync_mode`.

**Scheduled as a future concern** because it depends on deployment environment (Lambda scheduled events vs cron vs external scheduler). The consumer record reserves a `schedule` field but no scheduling infrastructure is built in MVP.

### 4. IPropagationAdapter interface

```csharp
public interface IPropagationAdapter
{
    /// <summary>Apply the manifest to the target system.</summary>
    Task<SyncResult> SyncAsync(Consumer consumer, EmojiManifest manifest, CancellationToken ct);

    /// <summary>Declare what this adapter supports.</summary>
    PropagationCapabilities Capabilities { get; }
}
```

The adapter is a **per-consumer-type implementation**. There is no "generic" adapter — each target platform gets its own adapter class that implements `IPropagationAdapter`. The DI container resolves the correct adapter based on the consumer's `adapter_type` field.

**MVP adapter:** `LocalFilePropagationAdapter` writes the manifest JSON and copies asset files into a configured output directory. This proves the model end-to-end without any external platform. A downstream application can read the manifest and assets from the filesystem — exactly the "local consumption" pattern from VISION.md.

**Future adapters:** `SlackPropagationAdapter`, `DiscordPropagationAdapter`, `S3ManifestPropagationAdapter` — each implements the same interface. Platform-specific configuration lives on the consumer record (e.g., Slack workspace token, Discord guild ID), not in the adapter itself.

### 5. Capability declaration

```csharp
public sealed record PropagationCapabilities
{
    /// <summary>Adapter can create new emoji in the target.</summary>
    public bool SupportsCreate { get; init; } = true;

    /// <summary>Adapter can update existing emoji (e.g., metadata, asset).</summary>
    public bool SupportsUpdate { get; init; } = true;

    /// <summary>Adapter can delete emoji from the target.</summary>
    public bool SupportsDelete { get; init; } = false;

    /// <summary>Adapter can handle batch operations (multiple emoji in one call).</summary>
    public bool SupportsBatch { get; init; } = true;

    /// <summary>Adapter can sync metadata (tags, categories, owner) independently of assets.</summary>
    public bool SupportsMetadataSync { get; init; } = true;

    /// <summary>Maximum number of emoji this target can hold (null = unlimited).</summary>
    public int? MaxEmojiCount { get; init; }

    /// <summary>Maximum asset size in bytes this target accepts (null = unlimited).</summary>
    public int? MaxAssetSizeBytes { get; init; }

    /// <summary>Allowed image content types (null = any).</summary>
    public IReadOnlySet<string>? SupportedContentTypes { get; init; }
}
```

The sync orchestrator checks capabilities before calling the adapter:

- If an emoji's state is `removed` and the adapter does not support delete, the item is skipped (or flagged with a warning in the result).
- If the emoji count exceeds `MaxEmojiCount`, the sync fails early with a clear error.
- If an asset exceeds `MaxAssetSizeBytes`, that item is skipped.

Capabilities are **declared by the adapter**, not configured per consumer. They reflect the platform's real limitations (e.g., Slack has undocumented emoji limits, some platforms only accept PNG). The orchestrator reads capabilities and applies them as pre-flight checks before invoking `SyncAsync`.

### 6. Failure semantics: surface, don't auto-retry

Each sync produces a `SyncResult`:

```csharp
public sealed record SyncResult
{
    public required string ManifestId { get; init; }
    public required string ConsumerId { get; init; }
    public required SyncStatus Status { get; init; }       // success, partial, failed
    public required int TotalEmoji { get; init; }
    public required int SyncedEmoji { get; init; }
    public required int SkippedEmoji { get; init; }
    public required int FailedEmoji { get; init; }
    public required IReadOnlyList<SyncItemResult> Items { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime CompletedAt { get; init; }
}

public sealed record SyncItemResult
{
    public required string EmojiUid { get; init; }
    public required SyncItemStatus Status { get; init; }  // synced, skipped, failed
    public string? Reason { get; init; }                   // why skipped or failed
}

public enum SyncStatus { Success, Partial, Failed }
public enum SyncItemStatus { Synced, Skipped, Failed }
```

**Rules:**

- **Per-item granularity.** A sync is `Partial` when some items succeed and others fail. The operator can inspect individual item results and decide whether to retry.
- **No automatic retry in MVP.** If a sync fails, the result is recorded in the audit log with `action = "sync.completed"` and the consumer's `last_sync_status` field is updated. The operator retries manually by calling `POST /consumers/{id}/sync` again.
- **Failures are auditable.** Every sync (success, partial, or failed) writes an audit event. The `manifest_id` links the audit event to the specific sync run.
- **Idempotency by manifest ID.** A given `manifest_id` is only applied once per consumer. If the same manifest is somehow submitted twice, the second call is a no-op (the adapter detects the duplicate).

**Why no auto-retry:**
- The MVP has no message queue or background worker infrastructure.
- Auto-retry without a dead-letter queue risks silent failures and backlog accumulation.
- Manual retry with full visibility is the safer default for a solo-operator system.
- When on-change triggers are added (post-MVP), fire-and-forget sync with retry can be built on a queue — but that infrastructure is not justified for the first iteration.

### 7. Consumer model (record shape for registration)

A consumer is a DynamoDB entity keyed by `PK=CONSUMER#{id}`, `SK=META`:

```csharp
public sealed record Consumer
{
    public required string ConsumerId { get; init; }        // ULID
    public required string Name { get; init; }               // human label
    public required string AdapterType { get; init; }        // "local-file", "slack", "discord", etc.
    public required ConsumerSyncMode SyncMode { get; init; } // manual, on_change, scheduled
    public required bool OnChange { get; init; }             // trigger sync on emoji mutation?
    public string? Schedule { get; init; }                   // cron expression (future)
    public ManifestFilter? Filter { get; init; }             // which emoji this consumer receives
    public IReadOnlyDictionary<string, string>? Config { get; init; } // adapter-specific config
    public ConsumerSyncStatus LastSyncStatus { get; init; }
    public string? LastManifestId { get; init; }
    public DateTime? LastSyncAt { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public enum ConsumerSyncMode { Manual, OnChange, Scheduled }
public enum ConsumerSyncStatus { NeverSynced, Success, Partial, Failed }

public sealed record ManifestFilter
{
    public IReadOnlyList<string>? Tags { get; init; }
    public IReadOnlyList<string>? Categories { get; init; }
    public string? Owner { get; init; }
    public string State { get; init; } = "active";  // "active", "deprecated", or "active,deprecated"
}
```

The consumer record is a domain model. Its DynamoDB key layout, repository interface, and API endpoints are implementation details deferred to the code phase. This ADR defines the conceptual shape so that the interface contract and sync flow can be specified unambiguously.

### 8. End-to-end sync flow

```
1. POST /consumers/{id}/sync
       │
2. Resolve consumer record (DynamoDB GET)
       │
3. Build manifest (from IEmojiIndex, filtered by consumer.ManifestFilter)
       │
4. Resolve adapter (DI: IPropagationAdapter by consumer.AdapterType)
       │
5. Pre-flight: validate against adapter.Capabilities
       │
6. adapter.SyncAsync(consumer, manifest)
       │
       │  For each emoji in manifest:
       │    ├─ Read asset from IAssetStore.GetAsync(uid)
       │    └─ Create/update/delete in target
       │
7. Record SyncResult
       │
8. Update consumer (last_sync_status, last_manifest_id, last_sync_at)
       │
9. Write audit event (action = "sync.completed")
       │
10. Return SyncResult to caller
```

## Consequences

**Positive:**

- **Locally testable.** The `LocalFilePropagationAdapter` requires only a filesystem. Full sync flows can be tested in CI with DynamoDB Local and a temp directory. No external platform credentials needed.
- **Zero new infrastructure.** No queues, no event buses, no scheduler. The sync runs in-process during the HTTP request. All state lives in DynamoDB (consumer records, audit events).
- **Extensible by design.** Adding a new platform means writing one adapter class and registering it in DI. The sync orchestrator, manifest builder, and consumer model don't change.
- **Honest about platform differences.** `PropagationCapabilities` makes platform limitations visible and gives the operator clear signals about what will and won't work before a sync runs.
- **Audit-grade visibility.** Every sync produces an audit event with per-item results. The operator always knows what was synced, what failed, and why.
- **Interface mirrors existing patterns.** `IPropagationAdapter` follows the same DI-friendly interface pattern as `IAssetStore`, `IEmojiIndex`, and `IAuditLog` — it is a domain interface implemented in Infrastructure, registered as a singleton.

**Negative:**

- **Synchronous execution in the HTTP request.** A sync with many emoji and large assets can exceed request timeouts. For MVP, this is acceptable because emoji counts are in the hundreds, not thousands. When scale demands it, the sync can be moved to a background job — the `SyncResult` shape and audit event are already designed for async completion.
- **Adapters are in-process.** A failing adapter (e.g., Slack API timeout) blocks the sync and returns an error to the caller. There is no retry mechanism in v1. The operator retries manually. This is acceptable for MVP because manual sync is the only trigger and failures are surfaced immediately.
- **No delta sync in v1.** The manifest includes all matching emoji on every sync. For hundreds of emoji, this is fine. When counts reach thousands, a delta mechanism (sync only what changed since `last_sync_at`) can be added as a manifest option — the consumer model already carries `last_manifest_id` and `last_sync_at` to support this.

## Alternatives Considered

| Alternative                                               | Why rejected                                                                                                                                                                                                                                                                                                                                                                                |
|-----------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Pure pull model (consumer polls `GET /emoji`)**         | Shifts complexity to every consumer. Each consumer must implement its own polling, deduplication, and error handling. The central service loses visibility into propagation state. VISION.md explicitly calls for the service to push/publish, not just serve queries.                                                                                                                      |
| **Event-driven (SNS/SQS/EventBridge)**                    | Adds always-on infrastructure cost and operational complexity. Requires an event schema, a dead-letter queue, and worker infrastructure. Correct long-term choice for production scale, but violates zero-cost-when-idle and local-testability goals for MVP. The `IPropagationAdapter` interface is designed so an event-driven sync trigger can be added later without changing adapters. |
| **Adapter-per-emoji (SyncAsync for one emoji at a time)** | Would force the sync orchestrator to loop over emoji and call the adapter N times, preventing batch-aware adapters from optimizing (e.g., Slack's bulk upload API). The manifest-based approach lets the adapter decide whether to process items individually or in bulk.                                                                                                                   |
| **Adapter writes directly to IAssetStore**                | Would blur the boundary between the registry and the propagation layer. Asset storage is a separate concern (local filesystem today, S3 tomorrow). Adapters read assets; they don't produce them.                                                                                                                                                                                           |
| **Capabilities as a runtime check inside SyncAsync**      | Would mean the adapter throws mid-sync for unsupported operations, potentially after partial work. Pre-flight capability checks let the orchestrator fail fast with a clear error before any side effects.                                                                                                                                                                                  |
| **Auto-retry with exponential backoff in MVP**            | Requires a background job system, retry state management, and a dead-letter mechanism — all infrastructure that violates the zero-cost-when-idle constraint. Manual retry with audit visibility is the simpler starting point.                                                                                                                                                              |
| **GraphQL or streaming sync**                             | Over-engineered for the MVP. A JSON manifest with file references is trivially debuggable, cachable, and testable.                                                                                                                                                                                                                                                                          |
