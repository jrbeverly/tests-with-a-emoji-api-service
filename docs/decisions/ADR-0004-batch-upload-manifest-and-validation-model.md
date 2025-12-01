# ADR-0004: Batch upload manifest and validation model

**Status:** Accepted

## Context

VISION.md section 2 requires batch upload as a core capability, not a future enhancement: "The service must support batch uploading. Batch upload is a core requirement." The batch workflow must support per-item metadata (alias, display name, tags, categories, owner), validation before finalization, and sensible error handling when some items in a batch fail.

Without a documented manifest shape and two-phase workflow, batch upload would be implemented ad-hoc, with no standard way for clients to validate before applying, no idempotency guarantees, and no clear contract for partial-success behavior.

We need a single document that defines: the manifest format, the per-item fields, the validate-then-apply workflow, partial-success semantics, and idempotency strategy.

## Decision

### 1. Manifest format

Batch upload uses **multipart/form-data** with a JSON manifest part and zero or more image file parts.

**Why multipart over zip:**
- Standard HTTP mechanism for mixed file + metadata uploads.
- .NET Minimal API has first-class `IFormFile` support.
- No server-side zip extraction or temp-file management needed.
- Each file can be validated independently before the manifest is fully parsed.

**Request structure:**

```
POST /emoji/batch/validate
Content-Type: multipart/form-data

--boundary
Content-Disposition: form-data; name="manifest"
Content-Type: application/json

{
  "items": [
    {
      "file_key": "approved",
      "alias": "approved",
      "display_name": "Approved Stamp",
      "description": "A green approval stamp for document reviews",
      "tags": ["approve", "stamp", "workflow"],
      "categories": ["workflow"],
      "owner": "docs-team"
    },
    {
      "file_key": "shipit",
      "alias": "shipit",
      "display_name": "Ship It",
      "description": "Ready to deploy",
      "tags": ["deploy", "release"],
      "categories": ["workflow"],
      "owner": "eng-platform"
    }
  ]
}

--boundary
Content-Disposition: form-data; name="approved"; filename="approved.png"
Content-Type: image/png

<binary data>

--boundary
Content-Disposition: form-data; name="shipit"; filename="shipit.gif"
Content-Type: image/gif

<binary data>

--boundary--
```

Each item's `file_key` matches the form field name of its corresponding file part. Items without a matching file part are flagged as validation errors.

### 2. Per-item manifest fields

Each item in the manifest `items` array supports these fields:

| Field          | Type     | Required | Description                                              |
|----------------|----------|----------|----------------------------------------------------------|
| `file_key`     | string   | Yes      | Matches the form field name of the item's image file     |
| `alias`        | string   | Yes      | The primary alias for this emoji                         |
| `display_name` | string   | No       | Human-readable display name; defaults to alias if absent |
| `description`  | string   | No       | Longer description for search and discovery              |
| `tags`         | string[] | No       | Searchable tags (max 20 per item, each max 64 chars)     |
| `categories`   | string[] | No       | Grouping categories (max 10 per item, each max 64 chars) |
| `owner`        | string   | No       | Owner or team identifier                                 |

### 3. File constraints

Files in the batch upload are subject to these constraints:

| Constraint             | Limit                            | Notes                                                   |
|------------------------|----------------------------------|---------------------------------------------------------|
| Max file size          | 256 KB                           | Per file. Emoji images are typically well under this.   |
| Supported formats      | PNG, GIF, JPG, SVG               | SVG limited to 64 KB due to parsing complexity          |
| Max files per batch    | 100                              | Practical limit for a single HTTP request               |
| Max total request size | 25 MB                            | Aligned with API Gateway limits                         |
| Image dimensions       | 128x128 recommended, max 512x512 | Resizing or rejection policy deferred to implementation |

### 4. Validate phase

**Endpoint:** `POST /emoji/batch/validate`

**Behavior:**
- Parses the manifest JSON and file parts.
- Validates every item against the full rule set: file presence, file format, file size, alias uniqueness, alias availability (not reserved, not blocked, not already active), field length limits, tag/category counts.
- Persists nothing to the emoji or alias tables.
- Generates a `manifest_id` (ULID) and stores the validated manifest temporarily (see idempotency below).
- Returns per-item issues with item-level granularity.

**Response (200 OK):**

```json
{
  "manifest_id": "01JABC123XYZ",
  "total_items": 2,
  "valid_items": 1,
  "invalid_items": 1,
  "items": [
    {
      "index": 0,
      "file_key": "approved",
      "status": "valid"
    },
    {
      "index": 1,
      "file_key": "shipit",
      "status": "invalid",
      "issues": [
        {
          "field": "alias",
          "code": "alias_reserved",
          "message": "Alias 'shipit' is reserved and cannot be claimed"
        }
      ]
    }
  ]
}
```

| Response field  | Type  | Description                                       |
|-----------------|-------|---------------------------------------------------|
| `manifest_id`   | ULID  | Stable identifier for this validated manifest     |
| `total_items`   | int   | Total items in the manifest                       |
| `valid_items`   | int   | Count of items with no issues                     |
| `invalid_items` | int   | Count of items with one or more issues            |
| `items`         | array | Per-item status (`valid` or `invalid`) and issues |

Item-level issue codes:

| Code                  | Meaning                                          |
|-----------------------|--------------------------------------------------|
| `missing_file`        | No file part matches the item's `file_key`       |
| `unsupported_format`  | File is not PNG, GIF, JPG, or SVG                |
| `file_too_large`      | File exceeds the size limit                      |
| `alias_taken`         | Alias is already active for another emoji        |
| `alias_reserved`      | Alias name is reserved                           |
| `alias_blocked`       | Alias name is blocked                            |
| `alias_invalid`       | Alias contains invalid characters or is too long |
| `field_too_long`      | A field exceeds its maximum length               |
| `too_many_tags`       | More than 20 tags on an item                     |
| `too_many_categories` | More than 10 categories on an item               |

### 5. Apply phase

**Endpoint:** `POST /emoji/batch/apply`

**Request:**

```json
{
  "manifest_id": "01JABC123XYZ"
}
```

The apply phase accepts only a `manifest_id` — the manifest data was already uploaded and validated in the validate phase. This avoids re-uploading files and ensures the apply operates on the exact data that was validated.

**Behavior:**
- Retrieves the stored manifest by `manifest_id`. Returns 404 if the manifest is not found (never validated, expired, or already applied and pruned).
- Processes only the items that passed validation (`status: "valid"`). Items that failed validation are silently skipped.
- Creates emoji records (in `Pending` state per ADR-0002), creates alias records (in `Active` state, primary alias linked to the emoji), and writes audit events (`emoji.upload` for each emoji, `alias.assign` for each alias).
- Writes items within a transaction or with per-item error handling. Items that fail at apply time (e.g., race condition where an alias was claimed between validate and apply) produce individual errors.
- Records the apply result in a manifest result item (see idempotency).
- Returns per-item outcomes.

**Response (200 OK):**

```json
{
  "manifest_id": "01JABC123XYZ",
  "applied_at": "2026-06-17T14:30:00Z",
  "total_items": 1,
  "succeeded": 1,
  "failed": 0,
  "items": [
    {
      "index": 0,
      "file_key": "approved",
      "status": "created",
      "emoji_uid": "01JABC456DEF",
      "alias_id": "01JABC789GHI"
    }
  ]
}
```

### 6. Partial success semantics

The apply phase uses **partial-success** semantics:

- Each item is processed independently. Failure of one item does not prevent other items from succeeding.
- Items that passed validation but fail at apply time (e.g., alias race condition) are marked `failed` in the response with a reason code.
- Items that failed validation are not processed — they appear as `skipped` in the apply response.
- There is no rollback of already-committed items if a later item fails. The response reports exactly what succeeded and what did not.
- The caller can re-submit the manifest after correcting failed/skipped items by re-validating and re-applying (with a new `manifest_id`).

Item-level apply outcomes:

| Status    | Meaning                                                     |
|-----------|-------------------------------------------------------------|
| `created` | Emoji and alias created successfully                        |
| `failed`  | Apply-time error (e.g., alias race, storage write failure)  |
| `skipped` | Item failed validation in the validate phase; not attempted |

### 7. Idempotency

**Manifest ID as idempotency key:**

- A `manifest_id` is generated by the validate phase and stored temporarily in DynamoDB as a `Manifest` item.
- The apply phase checks whether the `manifest_id` has already been applied. If it has, the previous apply result is returned (idempotent replay).
- If the manifest has not yet been applied, the apply proceeds and records the result.
- A manifest can only be applied once. Re-uploading the same data requires a new validate (and a new `manifest_id`).

**Manifest result storage (DynamoDB, same single table):**

| Attribute    | Value                                            |
|--------------|--------------------------------------------------|
| `PK`         | `MANIFEST#{manifest_id}`                         |
| `SK`         | `RESULT`                                         |
| `EntityType` | `ManifestResult`                                 |
| `GSI1PK`     | `MANIFEST`                                       |
| `GSI1SK`     | `{validated_at_iso}#{manifest_id}`               |
| `Data`       | Full manifest and result as JSON                 |
| `CreatedAt`  | `validated_at` (ISO 8601)                        |
| `UpdatedAt`  | `applied_at` (ISO 8601, null if not yet applied) |

The `MANIFEST` GSI1PK enables listing recent manifests (e.g., for an admin dashboard showing recent batch operations).

**Manifest state lifecycle:**

```
Validate → Stored (state: validated)
Apply    → Stored (state: applied, result populated)
Re-apply → Returns stored result (idempotent)
Expire   → Deleted via DynamoDB TTL (future concern, non-breaking)
```

Manifest result items are temporary operational records. They are not part of the permanent audit log. A TTL attribute can be added later (e.g., 7-day retention) without changing any other part of the design.

### 8. Audit events

Batch operations produce the following audit events (extending the vocabulary from ADR-0003):

| Action                 | Trigger                  | Subject                                                                                                   |
|------------------------|--------------------------|-----------------------------------------------------------------------------------------------------------|
| `emoji.batch_validate` | Validate phase completes | None (batch-level event, no single emoji subject; use a synthetic subject UID derived from `manifest_id`) |
| `emoji.batch_apply`    | Apply phase completes    | None (batch-level event, synthetic subject UID)                                                           |

Per-item events (`emoji.upload`, `alias.assign`) are also written for each successfully created emoji and alias during the apply phase, with the individual emoji UID as `subject_uid`. This ensures per-emoji audit history is complete and the per-emoji audit access pattern (`PK=EMOJI#{uid}`, `SK=AUDIT#...`) works for batch-uploaded emoji without special-casing.

For batch-level events, the `subject_uid` is derived from the `manifest_id` (e.g., `MANIFEST#{manifest_id}`) to keep the event shape consistent. The `before` snapshot is null for batch validate; the `after` snapshot is a summary of item counts and outcomes, not the full manifest (which is stored separately in the manifest result item).

## Consequences

**Positive:**
- Two-phase validate-then-apply gives administrators confidence before changes finalize.
- Multipart format is standard HTTP, well-supported by clients and .NET Minimal API.
- Per-item metadata (tags, categories, owner, description) from day one — no need to retrofit later.
- Partial-success semantics mean a single bad item does not block an entire batch. Callers can fix and retry only the failed items.
- Manifest ID as idempotency key prevents accidental double-applies. Stored results enable idempotent replay without re-processing.
- Manifest results stored in the same single table — no new infrastructure.
- Per-item audit events (`emoji.upload`, `alias.assign`) preserve complete per-emoji history regardless of batch vs individual upload path.

**Negative:**
- Storing the manifest between validate and apply introduces temporary state. This is intentional — the manifest is the bridge between the two phases. The state is small (< 500 KB per manifest) and short-lived.
- A race window exists between validate and apply (an alias could be claimed by another request). Apply-time failures handle this explicitly with per-item error outcomes. The window is acceptable for MVP; optimistic locking or reservation locks can narrow it later.
- Manifest result items accumulate. At MVP scale this is negligible. TTL-based cleanup can be added as a non-breaking change.
- The `MANIFEST` GSI1PK shares the same hot-partition concern as the `AUDIT` GSI1PK from ADR-0003. At MVP scale this is harmless. The same sharding strategy applies if batch volume grows.

## Alternatives Considered

| Alternative                             | Why rejected                                                                                                                                                                                                                                       |
|-----------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Zip file containing manifest + images   | Requires server-side zip extraction and temp file management. Adds complexity relative to multipart, which is natively supported by HTTP and .NET. Zip also requires the entire payload to be received before any validation can begin.            |
| Single-phase apply (no validate step)   | Violates VISION.md requirement for "validation before changes are finalized." Administrators need to see issues before committing.                                                                                                                 |
| Atomic all-or-nothing apply             | Rejects the entire batch if any item fails. Makes batch upload fragile — a single typo or alias conflict blocks 99 valid items. Partial success is the stated requirement from VISION.md.                                                          |
| Store manifest in S3                    | Adds a second storage system for temporary data. DynamoDB is simpler for structured, queryable manifest results and fits the existing single-table pattern. S3 is a viable alternative if manifest size grows beyond DynamoDB's 400 KB item limit. |
| Use a separate manifest table           | Violates single-table design preference. Manifest access patterns fit into the existing table and GSI.                                                                                                                                             |
| Generate `manifest_id` client-side      | Clients could forge or replay IDs. Server-generated ULIDs are time-sortable and under service control.                                                                                                                                             |
| Inline files as base64 in JSON manifest | Base64 encoding inflates payload size by ~33%. Multipart preserves binary format and is more efficient for file uploads.                                                                                                                           |
| Re-upload files in the apply phase      | Doubles upload bandwidth and creates a consistency risk (files could differ between validate and apply). Referencing the stored manifest by ID ensures the apply operates on the exact data that was validated.                                    |
