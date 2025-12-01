# DESIGN — Emoji API Service

System design overview, service boundaries, and delivery phases. For product requirements see [VISION.md](VISION.md). For architecture decisions see [docs/decisions/](docs/decisions/).

## System Summary

The Emoji API Service is a central registry and propagation system for custom emoji. It solves the common problem of emoji being trapped inside individual platforms (Slack, Discord, Atlassian) by providing one place to upload, organize, govern, and distribute custom emoji across many downstream systems.

Key design points (from [VISION.md](VISION.md)):

- **Central management, local consumption** — the service owns the administrative lifecycle; downstream apps cache locally rather than calling the service on every render.
- **Stable identity, flexible naming** — every emoji gets a server-assigned ULID that never changes. Aliases are human-facing names that can be remapped, retired, or protected independently of the underlying asset.
- **Propagation over runtime dependency** — the service publishes emoji data to registered consumers rather than requiring every app to perform live lookups.
- **Metadata-rich** — emoji are assets with searchable metadata (tags, categories, owner, status), not just image files.
- **Platform-neutral** — Slack, Discord, Atlassian, and others are integration targets, not the product itself.

## Service Boundaries (MVP)

The MVP is a single deployable service with three .NET projects and one test project. Boundaries follow the separation in `.claude/constraints/repository-structure.md` and the concrete layout in [CODEMAP.md](CODEMAP.md).

| Boundary           | Project                                         | Responsibility                                                                                                                                                                                                                                     |
|--------------------|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Api**            | `src/EmojiService/EmojiService.Api/`            | REST API surface. `Program.cs`, dependency injection, route registration, request/response DTOs, OpenAPI configuration. Depends on Domain and Infrastructure.                                                                                      |
| **Domain**         | `src/EmojiService/EmojiService.Domain/`         | Core business logic with no external dependencies. Emoji and Alias models, lifecycle state machines, `Result<T>`, value objects, domain errors. Does NOT depend on Api or Infrastructure.                                                          |
| **Infrastructure** | `src/EmojiService/EmojiService.Infrastructure/` | Adapters for external systems. DynamoDB repository (`DynamoDbEmojiRepository`), local asset store (`LocalAssetStore`), in-memory search index (`InMemoryEmojiIndex`), AWS SDK clients. Implements interfaces defined in Domain. Depends on Domain. |
| **Tests**          | `tests/EmojiService/EmojiService.Tests/`        | Unit and integration tests mirroring source structure. Domain tests (no mocks), application tests (fakes), infrastructure tests (DynamoDB Local via Testcontainers or direct), API tests (`WebApplicationFactory<Program>`).                       |

### Dependency direction

```
Api → Infrastructure → Domain
Api → Domain
Tests → (all projects under test)
```

Domain has no project dependencies. Infrastructure depends only on Domain. Api depends on both. This keeps the core logic testable in isolation and swappable (e.g., S3 for local storage) without touching Domain code.

## Milestones and Delivery Phases

Implementation is sequenced into four phases. Each phase produces working, tested code. Phases are named to match the labels used in issue tracking.

### Phase 1: Foundation

Scaffold the solution, projects, DI wiring, configuration, and a health-check endpoint. Prove that the toolchain works end-to-end: build, test, and run against DynamoDB Local.

| Deliverable           | Scope                                                                          |
|-----------------------|--------------------------------------------------------------------------------|
| Solution and projects | `EmojiService.sln`, all four `.csproj` files, project references               |
| Configuration         | `appsettings.json`, `AssetStoreOptions`, `DynamoDbOptions`, Options pattern DI |
| Health endpoint       | `GET /health` returning 200 OK with DynamoDB connectivity check                |
| Test infrastructure   | xUnit, FluentAssertions, `WebApplicationFactory<Program>`, CI passing          |
| DynamoDB Local        | Running in devcontainer, table created, SDK client wired                       |

### Phase 2: Model-A — Emoji CRUD and Aliases

Individual emoji upload, retrieval, update, and lifecycle transitions. Alias creation, resolution, remapping, and retirement. This phase proves the core registry.

| Deliverable           | Scope                                                                                                                                            |
|-----------------------|--------------------------------------------------------------------------------------------------------------------------------------------------|
| Emoji endpoints       | `POST /emoji`, `GET /emoji/{uid}`, `PUT /emoji/{uid}`, `DELETE /emoji/{uid}`                                                                     |
| Lifecycle transitions | Approve, deprecate, disable, enable via `POST /emoji/{uid}/transition`                                                                           |
| Alias endpoints       | `POST /aliases`, `GET /aliases/{name}`, `PUT /aliases/{name}`, `DELETE /aliases/{name}`                                                          |
| DynamoDB repository   | `DynamoDbEmojiRepository` implementing full single-table key schema ([ADR-0006](docs/decisions/ADR-0006-dynamodb-single-table-key-schema-v1.md)) |
| Asset storage         | `LocalAssetStore` implementing `IAssetStore` ([ADR-0008](docs/decisions/ADR-0008-local-asset-storage-and-configuration-model.md))                |
| ULID generation       | Server-assigned ULIDs for all entities ([ADR-0005](docs/decisions/ADR-0005-emoji-uid-generation-strategy.md))                                    |

### Phase 3: Model-B — Batch Upload and Audit

Batch upload with validate-then-apply, manifest idempotency, and the audit log. This phase proves the governance story.

| Deliverable     | Scope                                                                                                                    |
|-----------------|--------------------------------------------------------------------------------------------------------------------------|
| Batch validate  | `POST /batch/validate` — validate manifest + files, return per-item issues                                               |
| Batch apply     | `POST /batch/apply` — commit valid items, reject duplicates via manifest ID                                              |
| Audit events    | Immutable audit log for all administrative actions ([ADR-0003](docs/decisions/ADR-0003-audit-log-schema-and-storage.md)) |
| Audit endpoints | `GET /emoji/{uid}/audit`, `GET /audit` (global chronological listing)                                                    |

### Phase 4: Bridge — Search, Consumers, and Frontend

Search/discovery, downstream consumer registration, and the Vue.js management UI. This phase proves the propagation model end-to-end.

| Deliverable           | Scope                                                                                                                                              |
|-----------------------|----------------------------------------------------------------------------------------------------------------------------------------------------|
| Search endpoint       | `GET /emoji?q=...&tag=...&category=...&owner=...` via `IEmojiIndex` ([ADR-0007](docs/decisions/ADR-0007-search-and-discovery-strategy-for-mvp.md)) |
| Consumer registration | `POST /consumers`, `GET /consumers`, `GET /consumers/{id}`                                                                                         |
| Sync triggers         | `POST /consumers/{id}/sync` — initiate propagation to a registered consumer                                                                        |
| Frontend              | Vue.js management UI (`app/EmojiService/`) — upload, search, alias management                                                                      |

### Propagation Flow

The propagation model ([ADR-0009](docs/decisions/ADR-0009-propagation-model-and-sync-triggers.md)) uses push-triggered, manifest-driven sync with local asset access:

```
                        ┌─────────────────────────────┐
                        │      Sync Trigger            │
                        │  POST /consumers/{id}/sync   │
                        │  (manual, on-change, sched)  │
                        └─────────────┬───────────────┘
                                      │
                                      ▼
                        ┌─────────────────────────────┐
                        │      Build Manifest          │
                        │  IEmojiIndex.Search()        │
                        │  filtered by consumer config │
                        └─────────────┬───────────────┘
                                      │
                                      ▼  EmojiManifest (JSON)
                        ┌─────────────────────────────┐
                        │   IPropagationAdapter        │
                        │   SyncAsync(consumer,        │
                        │             manifest)        │
                        │                             │
                        │  ┌───────────────────────┐   │
                        │  │ LocalFileAdapter      │   │
                        │  │ SlackAdapter          │   │
                        │  │ DiscordAdapter  ...   │   │
                        │  └───────────────────────┘   │
                        └──────┬──────────────────────┘
                               │
                    reads      │      writes to
                    assets     │      target
                    from       │      platform
                               │
                        ┌──────┴──────┐    ┌──────────┐
                        │ IAssetStore │    │  Target   │
                        │ (local/S3)  │    │ Platform  │
                        └─────────────┘    └──────────┘
```

1. **Trigger** — manual `POST`, on-change hook, or future cron fires sync for a registered consumer.
2. **Build manifest** — the service queries `IEmojiIndex` with the consumer's filter (by tag, category, owner, state) and produces an `EmojiManifest` containing metadata + asset references for every matching emoji.
3. **Resolve adapter** — DI resolves the `IPropagationAdapter` for the consumer's `adapter_type` (e.g., `local-file`, `slack`).
4. **Pre-flight check** — the adapter's `PropagationCapabilities` are checked against the manifest (max emoji count, max asset size, supported content types).
5. **Sync** — the adapter iterates the manifest, reads asset bytes from `IAssetStore`, and creates/updates/deletes in the target.
6. **Record result** — a `SyncResult` with per-item status is returned, the consumer's `last_sync_status` is updated, and an audit event is written.

### Manifest Format

The manifest is a JSON document produced by `IManifestGenerator` that describes the set of emoji a consumer should have. It is generated from the current registry state (via `IEmojiIndex`) filtered by the consumer's `SubsetFilter`.

**Manifest version:** 1 (defined by `ManifestGenerator.CurrentManifestVersion`). The version is bumped when the manifest schema changes in a breaking way.

**Generation:** `IManifestGenerator.Generate(Consumer consumer)` — pure function over the in-memory index snapshot. Deterministic: the same consumer config and index state always produce the same emoji list (in UID-sorted order), with a unique `manifest_id` per invocation.

**Default filter behavior:** When the consumer's `SubsetFilter` is null or has no `State` field, the manifest includes all emoji in resolvable lifecycle states (`active` and `deprecated`). Emoji in `pending`, `disabled`, and `removed` states are excluded.

```jsonc
{
  "manifest_id": "01jabc123...",           // ULID, unique per generation
  "consumer_id": "01jdef456...",           // consumer this manifest is for
  "generated_at": "2026-06-18T12:00:00Z",
  "manifest_version": 1,
  "filter": {                              // the effective filter used (null = all resolvable)
    "tags": ["reaction"],
    "categories": null,
    "owner": "eng-team",
    "alias_prefix": null,
    "state": "active"
  },
  "emoji": [
    {
      "uid": "01jghi789...",               // 26-char lowercase ULID
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
  ]
}
```

**Emoji entry fields:**

| Field               | Type           | Source                       | Description                                                                               |
|---------------------|----------------|------------------------------|-------------------------------------------------------------------------------------------|
| `uid`               | string (ULID)  | `Emoji.Uid.Value`            | Stable, immutable emoji identifier                                                        |
| `primary_alias`     | string         | `Emoji.PrimaryAlias.Value`   | The emoji's primary human-facing name                                                     |
| `secondary_aliases` | string[]       | `Emoji.SecondaryAliases`     | Additional aliases that resolve to this emoji                                             |
| `display_name`      | string         | `Emoji.DisplayName`          | Human-readable display name                                                               |
| `description`       | string         | `Emoji.Description`          | Free-text description                                                                     |
| `content_type`      | string         | `Emoji.ContentType`          | MIME type of the asset (e.g., `image/png`)                                                |
| `asset_reference`   | string         | `Emoji.AssetReference`       | Opaque reference for `IAssetStore.GetAsync()`. Adapters use this to read the asset bytes. |
| `tags`              | string[]       | `Emoji.Tags`                 | Normalized (lowercase, trimmed) tags                                                      |
| `categories`        | string[]       | `Emoji.Categories`           | Normalized categories                                                                     |
| `owner`             | string         | `Emoji.Owner`                | Owning team or entity                                                                     |
| `state`             | string         | `Emoji.LifecycleState.Value` | Lifecycle state (`active`, `deprecated`, etc.)                                            |
| `created_at`        | DateTime (UTC) | `Emoji.CreatedAt`            | When the emoji was created                                                                |
| `updated_at`        | DateTime (UTC) | `Emoji.UpdatedAt`            | When the emoji was last modified                                                          |

**Pagination:** The manifest generator iterates all pages from `IEmojiIndex.Search()` (using cursor-based pagination) to collect every matching emoji. Results are emitted in UID-ascending order for deterministic output.

**Version history:**

| Version | Date       | Changes                                    |
|---------|------------|--------------------------------------------|
| 1       | 2026-06-18 | Initial schema. 13 fields per emoji entry. |

---

## Emoji Lifecycle

An emoji has a lifecycle beyond simply "present" or "absent." The lifecycle gives downstream consumers clear signals about an emoji's status.

```
                  ┌──────────┐
                  │  Pending  │
                  └─────┬─────┘
                        │
                  approve    reject
                        │
                  ┌─────▼─────┐
           ┌─────▷│  Active   │──────────────┐
           │      └──┬───┬───┘              │
           │         │   │                  │
      promote    deprecate             disable
           │         │   │                  │
           │   ┌─────▼┐  │          ┌───────▼──────┐
           │   │Depre- │  │          │   Disabled   │
           │   │cated  │  │          └───┬─────┬────┘
           │   └──┬───┘  │              │     │
           │      │      │              │     │
           │      │      └──────────────┘     │
           │      │        disable            │
           │      │                    remove │
           │      │              ┌────────────▼──────┐
           │      │              │     Removed       │
           │      │              └───────────────────┘
           │      │                    (terminal)
           └──────┘
            promote
```

### States

| State          | Meaning                                              | Resolvable by consumers?     |
|----------------|------------------------------------------------------|------------------------------|
| **Pending**    | Uploaded but not yet approved                        | No                           |
| **Active**     | Available for normal use                             | Yes                          |
| **Deprecated** | Still usable; flagged for future retirement          | Yes, with deprecation signal |
| **Disabled**   | Hidden from normal resolution; preserved for history | No                           |
| **Removed**    | Permanently deleted                                  | No (terminal)                |

### Transitions

| Transition  | From       | To         | Notes                                |
|-------------|------------|------------|--------------------------------------|
| `approve`   | Pending    | Active     | Standard activation                  |
| `reject`    | Pending    | Removed    | Rejected uploads are removed         |
| `deprecate` | Active     | Deprecated | Mark as superseded or outdated       |
| `promote`   | Deprecated | Active     | Restore a deprecated emoji to active |
| `disable`   | Active     | Disabled   | Temporary removal; aliases preserved |
| `disable`   | Deprecated | Disabled   | Escalate deprecation to hidden       |
| `enable`    | Disabled   | Active     | Restore a disabled emoji             |
| `remove`    | Disabled   | Removed    | Permanent deletion (terminal)        |

### Rules

- Only `Active` and `Deprecated` emoji are resolvable by downstream consumers.
- `Deprecated` resolution includes a deprecation signal so consumers can warn or migrate.
- Aliases of a `Disabled` emoji remain in their current alias state but do not resolve.
- `Removed` is terminal. Restoring a removed emoji requires admin-level recovery (future concern).
- Alias lifecycle is independent of emoji lifecycle. Retiring an alias does not change the emoji state.

### Emoji Identity

Every emoji receives a server-assigned ULID as its stable unique identifier. The UID is:

- **26 characters**, lowercase, no hyphens (e.g. `01jabc123xyz4567890abcdef`).
- **Time-sortable** — creation order is encoded in the UID itself, which simplifies downstream manifest diffing.
- **Immutable** — never reissued, even if the emoji is hard-deleted.
- **URL-safe** — fits cleanly in API paths (`/emoji/{uid}`) and DynamoDB keys (`PK=EMOJI#{uid}`).

All system-assigned identifiers (emoji UIDs, alias IDs, audit event IDs, manifest IDs) use ULID for consistency.

The full rationale, canonical form rules, generation strategy, and alternatives are defined in [ADR-0005](docs/decisions/ADR-0005-emoji-uid-generation-strategy.md).

### Alias State Machine

Aliases have their own state machine, separate from the emoji lifecycle:

| State       | Meaning                                                                  |
|-------------|--------------------------------------------------------------------------|
| **Active**  | The alias resolves to an emoji. Usable by consumers.                     |
| **Retired** | The alias is preserved but does not resolve. Name cannot be reused.      |
| **Blocked** | The alias name is forbidden. No emoji can claim it. Admin-only reversal. |

Alias states are documented here for completeness. The full alias model, reservation rules, and protection policies are defined in [ADR-0002](docs/decisions/ADR-0002-alias-model-reservations-and-lifecycle.md).

### Audit Log

Every administrative action — lifecycle transitions, alias changes, sync triggers, consumer registration, reservation changes — produces an immutable audit event.

Each event records: a unique `event_id` (ULID), the `actor`, the `action` (dot-delimited, e.g. `emoji.approve`, `alias.remap`), the `subject_uid` of the affected emoji, optional `before`/`after` snapshots, and an `occurred_at` timestamp.

Events are stored as DynamoDB items keyed for two access patterns:
- **Per-emoji history:** `PK=EMOJI#{subject_uid}`, `SK=AUDIT#{event_id}` — one query returns the full audit trail for a single emoji.
- **Global chronological listing:** `GSI1PK=AUDIT`, `GSI1SK={occurred_at_iso}#{event_id}` — supports administrative review across all emoji.

The complete v1 table key schema (all entity types, GSI layout, type-prefix conventions) is defined in [ADR-0006](docs/decisions/ADR-0006-dynamodb-single-table-key-schema-v1.md).

Events are append-only and immutable. There is no update or delete path. No automatic retention policy is applied in v1; TTL-based expiry can be added later as a non-breaking change.

The full event schema, action vocabulary, key design, and retention stance are defined in [ADR-0003](docs/decisions/ADR-0003-audit-log-schema-and-storage.md).

### Batch Upload

Batch upload uses a validate-then-apply workflow: a multipart request carries a JSON manifest plus image files, validation returns per-item issues without persisting anything, and apply commits valid items with partial-success semantics. Each manifest receives a server-generated `manifest_id` (ULID) used for idempotency — a manifest can only be applied once.

The full manifest format, per-item fields, validation rules, idempotency strategy, and audit event extensions are defined in [ADR-0004](docs/decisions/ADR-0004-batch-upload-manifest-and-validation-model.md).

## Guidance Index

| Document                                                             | Purpose                                                                     |
|----------------------------------------------------------------------|-----------------------------------------------------------------------------|
| [VISION.md](VISION.md)                                               | Product direction, functional requirements, MVP scope                       |
| [CODEMAP.md](CODEMAP.md)                                             | Concrete directory layout, project names, naming conventions                |
| [.claude/CLAUDE.md](.claude/CLAUDE.md)                               | Development philosophy, quick-reference, anti-patterns                      |
| [.claude/rules/core-principles.md](.claude/rules/core-principles.md) | Invariant rules (zero-cost, service-scoped, TDD, immutability)              |
| [.claude/csharp/minimal-api.md](.claude/csharp/minimal-api.md)       | Minimal API endpoint pattern used in Api project                            |
| [.claude/csharp/error-modeling.md](.claude/csharp/error-modeling.md) | Result\<T\> pattern used in Domain project                                  |
| [.claude/csharp/testing.md](.claude/csharp/testing.md)               | xUnit patterns used in Tests project                                        |
| [.claude/guidelines/dynamodb.md](.claude/guidelines/dynamodb.md)     | DynamoDB single-table design used in Infrastructure                         |
| [docs/mvp-validation.md](docs/mvp-validation.md)                     | MVP validation against VISION success criteria with evidence and known gaps |
