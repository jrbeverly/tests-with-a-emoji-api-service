# MVP Validation — VISION Success Criteria

Status of each VISION.md success criterion as of Milestone 5. Evidence drawn from integration tests, unit tests, endpoint implementations, ADRs, design docs, and the downstream test fixture.

---

## 1. Custom emoji can be uploaded once and reused across multiple systems

**Evidence:** Green

| Source                                                                    | Link                                                           |
|---------------------------------------------------------------------------|----------------------------------------------------------------|
| `UploadEmojiRouteTests`                                                   | `UploadEmojiRoute_ValidMultipart_ReturnsCreated`               |
| `CreateEmojiRoute`                                                        | `POST /api/v1/emoji` — individual upload endpoint              |
| `UploadEmojiRoute`                                                        | `POST /api/v1/emoji/upload` — multipart upload with file bytes |
| `ThinE2ETests.ThinE2E_UploadSyncResolve_DownstreamReadsLocalManifest`     | Upload → sync → local resolve proves reuse                     |
| `ThinE2ETests.ThinE2E_ConsumerWithSubsetFilter_OnlyReceivesMatchingEmoji` | Multiple consumers, filtered subsets                           |
| `SyncTriggerTests.ManualSync_ProducesPersistedResultWithCounts`           | Consumer receives synced emoji                                 |

An uploaded emoji lands in the registry, receives a stable UID, is stored locally via `IAssetStore`, and is pushed to registered downstream consumers via `IPropagationAdapter`. The Thin E2E test creates multiple consumers and proves each receives the manifest + assets.

---

## 2. Each emoji has a stable internal identity

**Evidence:** Green

| Source                      | Link                                                          |
|-----------------------------|---------------------------------------------------------------|
| ADR-0005                    | Emoji UID generation strategy (ULID)                          |
| `EmojiUid.cs`               | `EmojiUid` value object with validation                       |
| `EmojiUidTests`             | Valid/invalid ULID parsing                                    |
| `Emoji.cs`                  | `Emoji.Uid` is `init`-only, server-assigned                   |
| `DESIGN.md` §Emoji Identity | ULID properties (26-char, time-sortable, immutable, URL-safe) |
| `Program.cs:58-63`          | UIDs assigned server-side via `System.Ulid.NewUlid()`         |

Every emoji gets a server-assigned ULID (`System.Ulid.NewUlid()`) at creation time. The UID is immutable (`init`-only), never reissued, and serves as the primary key (`PK=EMOJI#{uid}`) in DynamoDB and the URL-safe path parameter (`/emoji/{uid}`).

---

## 3. Aliases can be managed independently from the underlying emoji asset

**Evidence:** Green

| Source                           | Link                                                                                                                                         |
|----------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| ADR-0002                         | Alias model, reservations, and lifecycle                                                                                                     |
| `AliasManagementTests`           | Add, retire, set-primary, resolve, search-by-prefix                                                                                          |
| `AliasRemapTests`                | Remap alias between emoji, atomic consistency, audit                                                                                         |
| `DESIGN.md` §Alias State Machine | Active / Retired / Blocked states                                                                                                            |
| `Alias.cs`                       | `Alias` value object with lifecycle state                                                                                                    |
| `IEmojiRepository`               | `AddAliasAsync`, `RetireAliasAsync`, `SetPrimaryAliasAsync`, `RemapAliasAsync`                                                               |
| Endpoints                        | `POST /emoji/{uid}/aliases`, `DELETE /emoji/{uid}/aliases/{alias}`, `PUT /emoji/{uid}/aliases/{alias}/primary`, `PUT /aliases/{name}/target` |

Aliases have independent state (`active`, `retired`, `blocked`) and protection levels (`normal`, `protected`). Remapping moves an alias between emoji without touching either emoji's asset data. Setting a primary alias changes which alias is displayed first. Retired aliases names are preserved but don't resolve.

---

## 4. Reserved and protected names prevent accidental collisions

**Evidence:** Green

| Source                                                                           | Link                                                                                    |
|----------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| `alias-policy.yaml`                                                              | Committed policy file (`reserved`, `reserved_prefixes`, `blocked`)                      |
| `IAliasPolicyService` / `AliasPolicyService`                                     | Loads policy on startup, validates on create/add/remap                                  |
| `AliasPolicyEnforcementTests`                                                    | Reserved (409/E600), reserved_prefix (409/E600), blocked (403/E601), protected-override |
| `GovernanceCrossCuttingTests.ReservedAlias_BlocksCreation_AndUnreservedSucceeds` | Cross-cutting verification                                                              |
| `GovernanceCrossCuttingTests.BlockedAlias_RejectedOnCreate_AndRemap`             | Blocked enforcement across all paths                                                    |
| ADR-0002 §Protection tiers                                                       | Normal, protected, blocked with override rules                                          |

Three tiers of name governance: `reserved` (409 Conflict, claimable with override), `reserved_prefixes` (prefix match blocks), and `blocked` (403 Forbidden, no override). Enforced at `POST /emoji`, `POST /emoji/{uid}/aliases`, and `PUT /aliases/{name}/target`. Protected aliases require explicit `overrideProtection: true` with a reason, and the override write a `alias.remap_override` audit event.

---

## 5. Batch upload is practical and metadata-aware

**Evidence:** Green

| Source                          | Link                                                           |
|---------------------------------|----------------------------------------------------------------|
| ADR-0004                        | Batch upload manifest and validation model                     |
| `ValidateBatchRouteTests`       | Per-item validation returns issues without persisting          |
| `ApplyBatchRouteTests`          | Commit valid items, manifest idempotency                       |
| `BatchAndSearchAcceptanceTests` | Seeds 20 emoji via batch apply, verifies all searchable        |
| `CuratedDataset` fixture        | 20-item curated dataset with tags, categories, owners per item |
| `POST /batch/validate`          | Multipart with JSON manifest + files, returns per-item issues  |
| `POST /batch/apply`             | Commits validated items, `manifest_id` for idempotency         |

Validate-then-apply workflow: `POST /batch/validate` checks a manifest + files and returns per-item issues (duplicate aliases, invalid names, missing files, unsupported types, reserved conflicts) without persisting. `POST /batch/apply` commits valid items with a server-generated `manifest_id` used for deduplication. All 20 curated emoji in `CuratedDataset` carry per-item tags, categories, and owner metadata.

---

## 6. Emoji can be searched by meaningful metadata

**Evidence:** Green

| Source                                                                                      | Link                                                                    |
|---------------------------------------------------------------------------------------------|-------------------------------------------------------------------------|
| ADR-0007                                                                                    | Search and discovery strategy (in-memory index for MVP)                 |
| `SearchEmojiRouteTests`                                                                     | Data-driven tests per filter dimension                                  |
| `BatchAndSearchAcceptanceTests`                                                             | Tag, category, owner, alias_prefix, state, combined filters, pagination |
| `IEmojiIndex` / `InMemoryEmojiIndex`                                                        | `Search()` with cursor-based pagination                                 |
| `GET /emoji?tag=...&category=...&owner=...&alias_prefix=...&state=...&limit=...&cursor=...` | Search endpoint                                                         |
| `AliasManagementTests` (search-by-alias-prefix)                                             | Secondary alias matching, retired-alias exclusion                       |

The search endpoint supports filtering by: `tag` (single or comma-separated AND), `category`, `owner`, `alias_prefix`, and `state` (defaults to `Active` only). Filters combine with AND semantics. Results are paginated with cursor-based pagination, returned newest-first (ULID time-sortable). The `BatchAndSearchAcceptanceTests` verify every filter dimension individually and in combination across a 20-item curated dataset. Response includes all required fields: `uid`, `primaryAlias`, `displayName`, `tags`, `categories`, `owner`, `createdAt`, `state`.

---

## 7. Downstream consumers can be registered and synchronized

**Evidence:** Green

| Source                                          | Link                                                                                        |
|-------------------------------------------------|---------------------------------------------------------------------------------------------|
| ADR-0009                                        | Propagation model and sync triggers                                                         |
| ADR-0010                                        | Consumer registration schema                                                                |
| `ConsumerCrudTests`                             | Full CRUD: create, get, list, update (pause/resume), delete, round-trip                     |
| `SyncTriggerTests`                              | Manual sync, on-change sync, result persistence, failure recording, pagination              |
| `POST /consumers`                               | Create with display_name, adapter, target_path, subset_filter, trigger_mode                 |
| `GET /consumers`, `GET /consumers/{id}`         | List (active by default), get by ID                                                         |
| `PUT /consumers/{id}`, `DELETE /consumers/{id}` | Update (name, filter, state transitions), delete                                            |
| `POST /consumers/{id}/sync`                     | Manual sync trigger (202 Accepted with result)                                              |
| `Consumer.cs`                                   | Domain validation: adapter whitelist, trigger modes, state machine, SubsetFilter validation |
| `DynamoDbConsumerRepository`                    | Persisted in DynamoDB                                                                       |
| `SyncOrchestrator`                              | `NotifyEmojiChangedAsync` — fire-and-forget on-change sync                                  |

Consumers support two trigger modes: `manual` (explicit `POST /consumers/{id}/sync`) and `on-change` (fire-and-forget after every emoji write). Each consumer can specify a `SubsetFilter` (tags, categories, owner, alias_prefix, state) to receive only matching emoji. Consumer state machine: `active` ↔ `paused` → `archived` (terminal). Paused consumers reject sync triggers with 409.

---

## 8. Consuming applications can use local data instead of calling the Emoji Service on every render

**Evidence:** Green

| Source                                                                | Link                                                                |
|-----------------------------------------------------------------------|---------------------------------------------------------------------|
| `ThinE2ETests.ThinE2E_UploadSyncResolve_DownstreamReadsLocalManifest` | Mechanical proof: remap → stale resolve → re-sync → updated resolve |
| `EmojiService.Downstream/Program.cs`                                  | Reads `manifest.json`, builds alias map in-memory, resolves locally |
| `LocalFilesystemPropagationAdapter`                                   | Writes `manifest.json` + asset files to consumer's target path      |
| `ManifestGenerator`                                                   | Produces deterministic manifest from index snapshot                 |
| DESGIN.md §Manifest Format                                            | 13-field emoji entries with `asset_reference`                       |
| `LocalFilesystemPropagationAdapterTests`                              | Verifies manifest JSON structure and asset file writes              |
| `POST /consumers/{id}/sync`                                           | Sync response includes `manifestId`, `totalEmoji`, `syncedEmoji`    |

The Thin E2E test proves the "central management, local consumption" model mechanically: upload emoji, register consumer, sync, resolve aliases via downstream (reads `manifest.json` from disk — no HTTP calls). Then remap an alias on the server, resolve before re-sync (stale manifest — still resolves to old target), re-sync, resolve again (updated manifest — resolves to new target). The downstream test fixture (`EmojiService.Downstream`) is a CLI tool that reads `manifest.json`, builds an in-memory alias→emoji map, and resolves aliases with zero server calls.

---

## 9. Sync results are visible and auditable

**Evidence:** Green

| Source                                                          | Link                                                                                                    |
|-----------------------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| `SyncTriggerTests.ManualSync_ProducesPersistedResultWithCounts` | Result has `resultId`, `consumerId`, `status`, `totalEmoji`, `syncedEmoji`, `failedEmoji`, `manifestId` |
| `SyncTriggerTests.ListSyncResults_ReturnsResultsWithPagination` | `GET /consumers/{id}/sync-results` with cursor pagination                                               |
| `SyncTriggerTests.ManualSync_UnwritableTarget_RecordsFailure`   | Failed sync persists with `status: "failed"` and `errorMessage`                                         |
| `SyncResultRecord.cs`                                           | Domain record with per-item status list                                                                 |
| `DynamoDbSyncResultRepository`                                  | Persists results to DynamoDB                                                                            |
| `GET /sync-results/{id}`                                        | Query individual sync result by ID                                                                      |
| `GET /consumers/{id}/sync-results`                              | List results per consumer (newest-first, paginated)                                                     |
| `AuditLogTests` + `SyncTriggerTests`                            | Sync triggers produce audit events                                                                      |

Every sync produces a persisted `SyncResultRecord` with a unique `resultId` (ULID), the `consumerId`, `manifestId`, `totalEmoji`/`syncedEmoji`/`failedEmoji` counts, per-item statuses, and an `errorMessage` on failure. Results are queryable by ID (`GET /sync-results/{id}`) and listed per consumer with cursor pagination (`GET /consumers/{id}/sync-results`). Sync failures are recorded (not silently dropped) — `ManualSync_UnwritableTarget_RecordsFailure` verifies the failed result is persisted and queryable.

---

## 10. The system can grow platform-specific integrations without becoming platform-specific itself

**Evidence:** Green

| Source                              | Link                                                                                               |
|-------------------------------------|----------------------------------------------------------------------------------------------------|
| ADR-0009                            | Propagation model — adapter pattern                                                                |
| `IPropagationAdapter`               | Single-method interface: `SyncAsync(consumer, manifest)`                                           |
| `LocalFilesystemPropagationAdapter` | First adapter implementation                                                                       |
| `DependencyInjection.cs:41`         | Adapter registered via DI (`AddSingleton<IPropagationAdapter, LocalFilesystemPropagationAdapter>`) |
| DESIGN.md §Propagation Flow         | Platform-agnostic flow: trigger → manifest → adapter → target                                      |
| `Consumer.cs`                       | `Adapter` field whitelist (`local-fs`) — new adapters add to whitelist                             |

The propagation architecture uses the `IPropagationAdapter` interface — a single `SyncAsync(Consumer, EmojiManifest)` method. The manifest format is platform-neutral JSON with 13 fields per emoji entry. Adding a new platform (e.g. Slack, Discord) requires implementing `IPropagationAdapter` and registering it in `DependencyInjection.cs`. The core registry, search index, manifest generation, and sync orchestration are adapter-agnostic. The `Consumer.Adapter` field (currently `local-fs` only) will be extended when new adapters ship.

---

## Known Gaps (Post-MVP Follow-Up)

These items are out of MVP scope per `VISION.md §Later Enhancements` and `VISION.md §Non-Goals`.

| Gap                                                                               | Proposed follow-up issue title                                               |
|-----------------------------------------------------------------------------------|------------------------------------------------------------------------------|
| No external platform adapters (Slack, Discord, Atlassian, etc.) — only `local-fs` | "Add Slack propagation adapter" / "Add Discord propagation adapter"          |
| No authentication or authorization middleware — actor hardcoded as `"system"`     | "Add authentication middleware and user identity"                            |
| No approval workflows for pending emoji                                           | "Add emoji approval workflow (pending → approve/reject)"                     |
| No richer permission model (admin vs. user roles)                                 | "Add role-based access control for emoji governance"                         |
| No team namespaces or multi-tenant boundaries                                     | "Add team namespace support for multi-tenant emoji isolation"                |
| No import from specific platforms (Slack, Discord, etc.)                          | "Add Slack emoji import adapter"                                             |
| No usage analytics or emoji popularity tracking                                   | "Add emoji usage analytics"                                                  |
| No duplicate detection or image quality validation                                | "Add duplicate image detection and quality checks"                           |
| No signed manifests or generated client SDKs                                      | "Add manifest signing" / "Generate typed SDK from OpenAPI spec"              |
| No moderation or approval workflows                                               | "Add emoji moderation queue"                                                 |
| Search index is in-memory only (rebuilt on cold start via DynamoDB Scan)          | "Add persistent search index (e.g. OpenSearch or DynamoDB GSI-based search)" |
| No frontend — Vue.js UI deferred to post-MVP                                      | "Build Vue.js emoji management UI"                                           |
| Audit log has no TTL-based retention — grows unbounded                            | "Add TTL-based audit log retention policy"                                   |
| DynamoDB Local is in-memory (data not persisted across devcontainer restarts)     | "Add persistent DynamoDB Local volume mount option"                          |

---

## Summary

All 10 VISION success criteria are satisfied by the MVP implementation. The system demonstrates the core "central management, local consumption" model end-to-end: upload emoji with metadata → register downstream consumers → sync via manifest + local files → resolve aliases from local data with zero server calls. Governance (reserved names, lifecycle states, audit log) and propagation (adapter pattern, sync result tracking) are both in place.

The primary gap between MVP and full VISION is external platform adapters (Slack, Discord, etc.) — the propagation architecture is designed for it, but only the `local-fs` adapter has been implemented. See "Known Gaps" above for the full post-MVP punch list.
