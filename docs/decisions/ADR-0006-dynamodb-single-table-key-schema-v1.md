# ADR-0006: DynamoDB single-table key schema v1

**Status:** Accepted

## Context

The DynamoDB single-table layout drives every access pattern in the registry. ADR-0003 already defines audit event keys (`PK=EMOJI#{uid}`, `SK=AUDIT#{event_id}`; GSI1 for global chronological listing). ADR-0002 defines aliases as managed records with their own lifecycle, separate from emoji. ADR-0004 defines batch manifests with a server-generated `manifest_id` for idempotency. [ADR-0010](ADR-0010-consumer-registration-schema.md) defines consumer registration records for propagation targets.

Before writing any storage code, we must decide the complete v1 key schema — partition key, sort key, GSI layout, and type-prefix conventions — so that every downstream issue works against a fixed contract.

### v1 access patterns

| Pattern                        | Description                                                 |
|--------------------------------|-------------------------------------------------------------|
| Get emoji by UID               | Retrieve an emoji record by its stable UID                  |
| Get emoji by alias             | Resolve an alias name to its linked emoji                   |
| List aliases of an emoji       | Return all aliases that point to a given emoji UID          |
| List audit events for an emoji | Return the audit trail for one emoji (already decided)      |
| List audit events globally     | Chronological listing across all emoji (already decided)    |
| Get manifest by ID             | Idempotency check — has this manifest already been applied? |

**Search/discovery:** Decided in [ADR-0007](ADR-0007-search-and-discovery-strategy-for-mvp.md). The v1 search strategy uses an in-memory index (no new GSIs required). The schema reserves flexibility for additional GSIs if a future strategy (e.g., OpenSearch-backed indices stored in DynamoDB) warrants them.

## Decision

### 1. Single-table, one GSI

The entire service uses one DynamoDB table with one global secondary index.

| Attribute | Key                    | Purpose                                                              |
|-----------|------------------------|----------------------------------------------------------------------|
| `PK`      | Partition key (string) | Scopes related items together                                        |
| `SK`      | Sort key (string)      | Orders items within a partition; distinguishes item types via prefix |
| `GSI1PK`  | GSI1 partition key     | Alternate query entry points                                         |
| `GSI1SK`  | GSI1 sort key          | Alternate sort ordering within GSI1 partitions                       |

GSI1 is overloaded — different entity types use different GSI1PK values. This is the standard DynamoDB single-table pattern and does not cause collisions because queries always filter by a specific GSI1PK value.

### 2. Type-prefix conventions

All partition and sort keys use a `{TYPE}#{value}` format. The type prefix is a short, uppercase identifier followed by `#`. This convention is mandatory for every item in the table.

| Prefix      | Entity                | Description                                                               |
|-------------|-----------------------|---------------------------------------------------------------------------|
| `EMOJI#`    | Emoji                 | Emoji records; value is the emoji UID (ULID)                              |
| `ALIAS#`    | Alias                 | Alias records; value is the alias name (lowercase shortcode)              |
| `AUDIT#`    | Audit event           | Audit log entries; value is the event UID (ULID)                          |
| `MANIFEST#` | Batch manifest        | Manifest idempotency records; value is the manifest UID (ULID)            |
| `CONSUMER#` | Consumer registration | Consumer records for propagation targets; value is the consumer ID (ULID) |

In sort keys, the prefix also acts as a discriminator for `begins_with` queries within a partition.

### 3. Entity key layouts

#### Emoji

| Attribute    | Value                           |
|--------------|---------------------------------|
| `PK`         | `EMOJI#{uid}`                   |
| `SK`         | `META`                          |
| `EntityType` | `Emoji`                         |
| `GSI1PK`     | *(not set)*                     |
| `GSI1SK`     | *(not set)*                     |
| `Data`       | Full emoji domain model as JSON |

`SK=META` is used for singleton items — there is exactly one emoji record per UID. The full emoji domain object (aliases, metadata, lifecycle state, timestamps) is serialized into the `Data` attribute.

#### Alias

| Attribute    | Value                           |
|--------------|---------------------------------|
| `PK`         | `ALIAS#{name}`                  |
| `SK`         | `META`                          |
| `EntityType` | `Alias`                         |
| `GSI1PK`     | `EMOJI#{emoji_uid}`             |
| `GSI1SK`     | `ALIAS#{name}`                  |
| `Data`       | Full alias domain model as JSON |

Alias items live under their own partition (`ALIAS#{name}`) so that alias resolution is a direct `GetItem` — the most common runtime access pattern. The alias domain model includes `emoji_uid` (the linked emoji), `state`, `is_primary`, and `protection`.

GSI1 provides the reverse mapping — given an emoji UID, find all aliases that point to it:

```
Query GSI1 where GSI1PK = EMOJI#{uid} AND begins_with(GSI1SK, "ALIAS#")
```

Because alias names are unique within GSI1PK, GSI1SK uses `ALIAS#{name}` rather than a timestamp for a stable sort order.

#### Audit event

| Attribute    | Value                          |
|--------------|--------------------------------|
| `PK`         | `EMOJI#{emoji_uid}`            |
| `SK`         | `AUDIT#{event_id}`             |
| `EntityType` | `AuditEvent`                   |
| `GSI1PK`     | `AUDIT`                        |
| `GSI1SK`     | `{occurred_at_iso}#{event_id}` |
| `Data`       | Full audit event as JSON       |

This matches the decision in ADR-0003. Audit events are stored under the emoji partition for per-emoji history queries (`begins_with(SK, "AUDIT#")`), with GSI1 providing global chronological listing.

`GSI1PK=AUDIT` is a single, well-known partition key. At MVP scale this is harmless. If write volume grows to thousands of events per second, the GSI1PK can be sharded (e.g., `AUDIT#2026-06` for monthly partitions) without changing the event shape or the per-emoji access pattern.

#### Batch manifest

| Attribute    | Value                               |
|--------------|-------------------------------------|
| `PK`         | `MANIFEST#{manifest_id}`            |
| `SK`         | `META`                              |
| `EntityType` | `Manifest`                          |
| `GSI1PK`     | *(not set)*                         |
| `GSI1SK`     | *(not set)*                         |
| `Data`       | Manifest status and summary as JSON |

Manifest items support idempotency: before applying a manifest, the service does a `GetItem(PK=MANIFEST#{id}, SK=META)`. If the item exists, the manifest was already applied and the request is rejected with the existing manifest status.

#### Consumer registration

| Attribute    | Value           |
|--------------|-----------------|
| `PK`         | `CONSUMER#{id}` |
| `SK`         | `META`          |
| `EntityType` | `Consumer`      |
| `GSI1PK`     | *(not set)*     |
| `GSI1SK`     | *(not set)*     |

Consumer records are singleton items keyed by consumer ID. Individual fields (`DisplayName`, `Adapter`, `TargetPath`, `SubsetFilter`, `TriggerMode`, `State`, `CreatedAt`, `UpdatedAt`) are stored as top-level attributes — following the same pattern as Emoji items. The `SubsetFilter` is stored as a JSON string (nested object with optional list-valued dimensions). A null filter is represented by the absence of the attribute.

v1 access patterns:

- **Get consumer by ID:** `GetItem(PK=CONSUMER#{id}, SK=META)`
- **List all consumers:** `Scan` with `FilterExpression: EntityType = "Consumer"`

No GSI is needed for v1. Consumer count is expected to be small (single digits to low tens), so a scan is acceptable. If consumer count grows, a `GSI1PK=CONSUMER` partition can be added without changing the item shape.

### 4. Complete v1 schema

| Entity      | PK                  | SK                 | GSI1PK              | GSI1SK                     |
|-------------|---------------------|--------------------|---------------------|----------------------------|
| Emoji       | `EMOJI#{uid}`       | `META`             | —                   | —                          |
| Alias       | `ALIAS#{name}`      | `META`             | `EMOJI#{emoji_uid}` | `ALIAS#{name}`             |
| Audit event | `EMOJI#{emoji_uid}` | `AUDIT#{event_id}` | `AUDIT`             | `{occurred_at}#{event_id}` |
| Manifest    | `MANIFEST#{id}`     | `META`             | —                   | —                          |
| Consumer    | `CONSUMER#{id}`     | `META`             | —                   | —                          |

### 5. Access pattern summary

| Pattern               | Operation                         | Key / Index                                                    |
|-----------------------|-----------------------------------|----------------------------------------------------------------|
| Get emoji by UID      | `GetItem`                         | PK=`EMOJI#{uid}`, SK=`META`                                    |
| Get emoji by alias    | `GetItem` alias → `GetItem` emoji | PK=`ALIAS#{name}`, SK=`META`; then PK=`EMOJI#{uid}`, SK=`META` |
| List aliases of emoji | `Query` GSI1                      | GSI1PK=`EMOJI#{uid}`, `begins_with(GSI1SK, "ALIAS#")`          |
| List audit for emoji  | `Query` table                     | PK=`EMOJI#{uid}`, `begins_with(SK, "AUDIT#")`                  |
| List audit globally   | `Query` GSI1                      | GSI1PK=`AUDIT`, `ScanIndexForward=false`                       |
| Get manifest by ID    | `GetItem`                         | PK=`MANIFEST#{id}`, SK=`META`                                  |
| Get consumer by ID    | `GetItem`                         | PK=`CONSUMER#{id}`, SK=`META`                                  |
| List all consumers    | `Scan`                            | `FilterExpression: EntityType = "Consumer"`                    |

### 6. GSI1 overload map

```
GSI1PK values:
  EMOJI#{uid}     → alias records (reverse lookup: emoji → aliases)
  AUDIT            → audit events (global chronological listing)
```

Queries always include a specific `GSI1PK` value, so these two uses never overlap.

### 7. Single-table design rationale

All five entity types live in one table, consistent with the project's DynamoDB single-table design preference (see `.claude/guidelines/dynamodb.md`). Benefits:

- **One table to manage** — one backup policy, one set of capacity settings, one Terraform resource.
- **No distributed transactions** — audit events for an emoji share the same partition as the emoji itself; a `TransactWriteItems` call can atomically write an emoji update and its audit event.
- **Zero-cost-when-idle** — on-demand billing with a single table means $0 when the service has no traffic.

## Consequences

**Positive:**
- Every v1 access pattern has a clear, documented key path. Downstream storage code can be written against this spec without ambiguity.
- Type prefixes (`EMOJI#`, `ALIAS#`, `AUDIT#`, `MANIFEST#`, `CONSUMER#`) are self-documenting in DynamoDB items, AWS Console views, and logs — no need to decode opaque keys.
- GSI1 is overloaded but clean: two distinct `GSI1PK` values (`EMOJI#{uid}` and `AUDIT`) with no overlap risk.
- Alias resolution is a direct `GetItem` — the most common runtime path for consumers resolving emoji by shortcode.
- Future entities (search indices, reservations) can be added by defining new type prefixes and, if needed, additional GSI1PK values — no schema migration required. Consumer registrations have already been added under the `CONSUMER#` prefix (ADR-0010) following this exact pattern.
- The `META` sort key convention for singleton items is simple and consistent across emoji, alias, and manifest entities.

**Negative:**
- Alias resolution requires two sequential `GetItem` calls (alias → emoji) rather than a single query. For v1, this is acceptable: DynamoDB `GetItem` is single-digit milliseconds, and the two-step path keeps the schema simple with no additional GSI.
- Listing aliases of an emoji requires a GSI query rather than a table query. The alias count per emoji is expected to be small (<10), so the GSI query is lightweight.
- The `AUDIT` GSI1PK creates a single hot partition for all audit events globally. At MVP scale this is harmless. If write volume reaches thousands of events per second, the partition can be sharded (e.g., `AUDIT#2026-06`) without changing the event shape or per-emoji access pattern — as noted in ADR-0003.
- Search/discovery access patterns (filter by tag, category, owner) are handled by the in-memory index defined in [ADR-0007](ADR-0007-search-and-discovery-strategy-for-mvp.md), which requires no schema changes. Future strategies (e.g., OpenSearch) may add GSIs if needed, and the schema supports that without breaking v1.

## Alternatives Considered

| Alternative                                                                                         | Why rejected                                                                                                                                                                                                                                                                                |
|-----------------------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Store aliases under emoji partition: `PK=EMOJI#{uid}`, `SK=ALIAS#{name}`                            | Listing aliases becomes a table query (no GSI needed), but alias resolution requires a GSI. Since alias resolution is the hotter path for consumer lookups, keeping it as a direct `GetItem` is preferred.                                                                                  |
| GSI-free design: dual-write alias items into both `PK=ALIAS#{name}` and `PK=EMOJI#{uid}` partitions | Covers both access patterns without a GSI, but dual-write introduces consistency risk (one write succeeds, the other fails). For v1, the GSI is simpler and more reliable.                                                                                                                  |
| Separate tables per entity (emoji, alias, audit, manifest)                                          | Violates the project's single-table design preference. Adds operational overhead (multiple backup policies, multiple Terraform resources) with no meaningful benefit at v1 scale.                                                                                                           |
| Store aliases as a JSON array on the emoji record                                                   | Rejected in ADR-0002. Aliases are managed records with their own lifecycle, not incidental strings. An array cannot support alias identity, audit trail, or independent state transitions.                                                                                                  |
| Use a composite alias SK: `SK=EMOJI#{uid}` instead of `SK=META`                                     | The emoji UID is already an attribute in the alias `Data` field and in `GSI1PK`. Encoding it in SK adds no query capability (we never query aliases by emoji UID on the table) and complicates the item identity. `SK=META` is simpler and consistent with the emoji and manifest patterns. |
| Add GSI2 for alias reverse lookup instead of overloading GSI1                                       | Each GSI adds cost (~$0.25 per million write requests for the additional index writes). At v1 scale, a single GSI with overloaded partitions is sufficient. Additional GSIs can be added later if access patterns diverge.                                                                  |
