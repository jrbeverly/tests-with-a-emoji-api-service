# ADR-0003: Audit log schema and storage

**Status:** Accepted

## Context

VISION.md section 11 requires governance and auditability: "The service should retain enough history to understand who changed what and when." DESIGN.md defines emoji lifecycle transitions (approve, reject, deprecate, promote, disable, enable, remove) and ADR-0002 defines alias state transitions (retire, reactivate, block, unblock) — all are sensitive administrative actions that must leave an audit trail.

Audit events are written by many code paths (lifecycle transitions, alias changes, sync triggers, consumer registration, reservation changes). The event shape and storage location must be fixed before write callers proliferate. If every caller invents its own log format, downstream consumers (audit UIs, compliance tooling, sync diffing) must reconcile inconsistent shapes.

We need a single, stable audit event contract and a DynamoDB key design that serves two access patterns: per-emoji history and global chronological listing.

## Decision

### 1. Event shape

Every audit event carries these fields:

| Field         | Type                | Required | Description                                                                        |
|---------------|---------------------|----------|------------------------------------------------------------------------------------|
| `event_id`    | ULID (string)       | Yes      | Unique, time-sortable event identifier                                             |
| `actor`       | string              | Yes      | Who performed the action (user ID, system principal)                               |
| `action`      | string              | Yes      | Dot-delimited action name (see vocabulary below)                                   |
| `subject_uid` | ULID (string)       | Yes      | The emoji UID this event pertains to                                               |
| `before`      | JSON (string?)      | No       | Snapshot of the affected record before the change. `null` for create events.       |
| `after`       | JSON (string?)      | No       | Snapshot of the affected record after the change. `null` for delete/remove events. |
| `occurred_at` | DateTime (ISO 8601) | Yes      | When the event happened (server-side timestamp)                                    |
| `reason`      | string?             | No       | Human-readable reason for the action                                               |

`before` and `after` store the relevant record snapshot as serialized JSON — an emoji record, an alias record, a reservation rule, or a consumer registration, depending on the action. They are optional to avoid bloating events where the snapshot is trivially reconstructable from the current record, but they SHOULD be populated for state transitions (lifecycle, alias remapping).

### 2. Action vocabulary (v1)

Actions use a `domain.verb` naming convention. The domain prefix groups related actions.

**Emoji lifecycle:**

| Action            | Trigger                                     |
|-------------------|---------------------------------------------|
| `emoji.upload`    | New emoji uploaded (enters Pending)         |
| `emoji.approve`   | Pending → Active                            |
| `emoji.reject`    | Pending → Removed                           |
| `emoji.deprecate` | Active → Deprecated                         |
| `emoji.disable`   | Active → Disabled, or Deprecated → Disabled |
| `emoji.enable`    | Disabled → Active                           |
| `emoji.promote`   | Deprecated → Active                         |
| `emoji.remove`    | Disabled → Removed                          |

**Alias management:**

| Action              | Trigger                                        |
|---------------------|------------------------------------------------|
| `alias.assign`      | New alias created and linked to an emoji       |
| `alias.remap`       | Existing alias reassigned to a different emoji |
| `alias.set_primary` | Primary alias designation changed for an emoji |
| `alias.retire`      | Alias retired (Active → Retired)               |
| `alias.reactivate`  | Retired alias reactivated (Retired → Active)   |
| `alias.block`       | Alias blocked (Active → Blocked)               |
| `alias.unblock`     | Alias unblocked (Blocked → Active)             |

**Metadata:**

| Action                  | Trigger                                                    |
|-------------------------|------------------------------------------------------------|
| `emoji.update_metadata` | Emoji metadata changed (tags, description, category, etc.) |

**Reservations and policy:**

| Action               | Trigger                                        |
|----------------------|------------------------------------------------|
| `reservation.create` | A reserved alias or reserved prefix is created |
| `reservation.delete` | A reserved alias or reserved prefix is removed |

**Sync and consumers:**

| Action              | Trigger                                    |
|---------------------|--------------------------------------------|
| `sync.trigger`      | Sync to a downstream consumer is initiated |
| `consumer.register` | A new downstream consumer is registered    |
| `consumer.update`   | A consumer's configuration is changed      |
| `consumer.remove`   | A downstream consumer is removed           |

### 3. DynamoDB storage

Audit events live in the same single table as emoji, alias, and consumer records. Each audit event is one DynamoDB item.

**Key design:**

| Access pattern                     | Index | PK                    | SK                             |
|------------------------------------|-------|-----------------------|--------------------------------|
| List audit events for an emoji UID | Table | `EMOJI#{subject_uid}` | `AUDIT#{event_id}`             |
| List audit events globally by time | GSI1  | `AUDIT`               | `{occurred_at_iso}#{event_id}` |

**Item layout:**

| Attribute    | Value                                                   |
|--------------|---------------------------------------------------------|
| `PK`         | `EMOJI#{subject_uid}`                                   |
| `SK`         | `AUDIT#{event_id}`                                      |
| `EntityType` | `AuditEvent`                                            |
| `GSI1PK`     | `AUDIT`                                                 |
| `GSI1SK`     | `{occurred_at_iso}#{event_id}`                          |
| `Data`       | Full event as JSON (all fields above)                   |
| `CreatedAt`  | `occurred_at` (ISO 8601)                                |
| `UpdatedAt`  | `occurred_at` (same as CreatedAt; events are immutable) |

**Why reverse chronological for GSI1?** The GSI1SK uses ISO 8601 timestamps (e.g., `2026-06-17T14:30:00Z#01J...`). DynamoDB sort keys sort lexicographically ascending, so a `Query` on GSI1 with `ScanIndexForward = false` returns the most recent events first — the natural order for an audit viewer.

### 4. Immutability

Audit events are append-only and immutable. Once written, an audit event MUST NOT be updated or deleted through normal application code. There is no `UpdateAuditEvent` or `DeleteAuditEvent` operation.

If an audit event was written with incorrect data (e.g., a bug), a corrective event with a distinct `event_id` is written instead. The original event remains as a historical record.

### 5. Retention

No automatic retention policy in v1. Audit events are retained indefinitely.

DynamoDB TTL is available as a future mechanism: a `ttl` attribute (epoch seconds) can be set on audit items, and DynamoDB will expire them automatically. Adding TTL later is a non-breaking change — the attribute is simply added to new items (and optionally backfilled). The decision to add a retention window (e.g., 2 years) is deferred until operational needs and cost data are known.

## Consequences

**Positive:**
- Single, stable event contract — all write callers produce the same shape, all readers consume it.
- ULID `event_id` is time-sortable without a timestamp index, useful for client-side ordering within a partition.
- Per-emoji partition key (`EMOJI#{uid}`) keeps audit history for one emoji co-located — a single query with `begins_with(SK, "AUDIT#")` returns the full history.
- Global GSI (`AUDIT`) supports administrative review across all emoji without scanning.
- Immutability eliminates an entire class of bugs (accidental audit overwrite, race-condition updates).
- Deferring retention policy avoids premature complexity.

**Negative:**
- `before`/`after` snapshots duplicate data that also exists on the current record. This is intentional — audit data must be independent of the current state so it survives record deletion and is not affected by later mutations.
- The `AUDIT` GSI1PK creates a single partition for all audit events globally. At MVP scale this is harmless. At high write volume (thousands of events per second), it becomes a hot partition. If this occurs, the GSI1PK can be sharded (e.g., `AUDIT#2026-06` for monthly partitions) without changing the event shape or the per-emoji access pattern.
- Indefinite retention means unbounded storage growth. Audit events are small (~1 KB each), so even 1 million events is ~1 GB — negligible at DynamoDB pricing. A retention policy should be revisited when the event count exceeds 10 million or cost becomes material.

## Alternatives Considered

| Alternative                                            | Why rejected                                                                                                                                                                    |
|--------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Store audit events as a JSON array on the emoji record | Array grows unbounded; DynamoDB item size limit (400 KB) caps history; no global query path without scanning.                                                                   |
| Use a separate audit table                             | Violates single-table design preference; adds a second table to manage, back up, and pay for. The access patterns fit naturally into the existing table.                        |
| Store audit events in S3 (JSONL files)                 | Adds a second storage system; query latency is higher; no DynamoDB-style query expressions. Viable as a cold-storage archiving layer later, but not for the primary write path. |
| Include `before`/`after` in all events                 | Bloats create and delete events where one snapshot is trivially null. Making them optional lets callers omit the snapshot when it adds no value.                                |
| Use `event_id` as the table sort key without a prefix  | Without the `AUDIT#` prefix, `begins_with(SK, "AUDIT#")` cannot distinguish audit events from other item types sharing the same emoji partition.                                |
| Enforce retention from day one                         | Premature. We do not yet know the event volume, cost profile, or retention needs. Adding TTL later is a non-breaking attribute addition.                                        |
| Shard the GSI1PK from the start                        | Premature optimization. The single `AUDIT` partition handles MVP scale. Sharding can be added without event shape changes if hot-partition pressure appears.                    |
