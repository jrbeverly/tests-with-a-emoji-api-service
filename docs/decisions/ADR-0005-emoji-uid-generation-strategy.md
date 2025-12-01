# ADR-0005: Emoji UID generation strategy

**Status:** Accepted

## Context

VISION.md requires every emoji to have a stable system-assigned UID that survives alias and metadata changes. The format influences storage keys (ADR-0003 uses `EMOJI#{uid}` as the DynamoDB partition key), audit event references (`subject_uid`), alias records (ADR-0002 — `EmojiId` field), and downstream manifest keys.

The existing decisions already use ULID for alias IDs (ADR-0002), audit event IDs (ADR-0003), and manifest IDs (ADR-0004). The emoji UID is the last core identifier to standardize.

We need to pick one format and define its canonical form and lifecycle rules before implementation writes the first emoji record.

## Decision

### 1. Format

The emoji UID is a **ULID** (Universally Unique Lexicographically Sortable Identifier).

- 26 characters, Crockford base32 encoding.
- Time-sortable: the first 10 characters encode a 48-bit millisecond timestamp.
- The remaining 16 characters are cryptographically random.
- URL-safe: no special characters, unambiguous alphabet (no `I`, `L`, `O`, `U`).

### 2. Canonical string form

The canonical form is **lowercase, no hyphens**.

```
01jabc123xyz4567890abcdef
```

Storage and transmission rules:

- Stored as a plain string in DynamoDB (key prefix: `EMOJI#{uid}`).
- Exposed in API responses as a lowercase string.
- Accepted in API requests case-insensitively; normalized to lowercase on ingestion.
- No hyphens, no base64, no binary encoding in external representations.

### 3. Generation

ULIDs are generated server-side at emoji record creation time. Clients never supply or suggest UIDs.

For .NET, the `System.Ulid` type (available in .NET 8 via the `Ulid` NuGet package) or an equivalent well-audited library is used. The generator MUST use a monotonic random source to prevent collisions within the same millisecond.

### 4. Immutability

A UID, once assigned to an emoji record, is **never reissued** — even if the emoji is hard-deleted (reaches `Removed` state).

- Removing an emoji deletes the record; the UID is not recycled.
- There is no "reuse UID" or "reassign UID" operation.
- Audit events referencing a removed emoji's UID remain historically valid.

### 5. Relationship to other identifiers

| Identifier     | Format | Scope                       | Decided in |
|----------------|--------|-----------------------------|------------|
| Emoji UID      | ULID   | Identifies an emoji asset   | This ADR   |
| Alias ID       | ULID   | Identifies an alias record  | ADR-0002   |
| Audit event ID | ULID   | Identifies an audit event   | ADR-0003   |
| Manifest ID    | ULID   | Identifies a batch manifest | ADR-0004   |

All system-assigned identifiers use ULID. There is no mixing of GUID and ULID in the same domain.

## Consequences

**Positive:**

- Consistent with all other system identifiers (alias IDs, audit event IDs, manifest IDs). No mixing of formats.
- Time-sortable by creation time without a separate timestamp attribute. This is useful for downstream manifest diffing: consumers receiving a list of emoji can sort by UID to detect additions and removals efficiently.
- URL-safe without encoding. Fits naturally in REST API paths (`/emoji/{uid}`) and as a query parameter.
- 26 characters is compact for storage and transmission. GUID v7 is 36 characters with hyphens, 32 without — ULID saves 6–10 characters per key.
- Fits naturally in DynamoDB key patterns: `PK=EMOJI#{uid}`, `SK=METADATA`. Lexicographic sort order of the 26-char string matches creation-time order without a separate sort key prefix.
- Monotonic generation within a millisecond prevents collisions without coordination.

**Negative:**

- Requires a library dependency (`Ulid` NuGet package) — minimal cost, well-maintained, matches the dependency already needed for other ULID uses.
- ULID is less widely supported than GUID in some tooling (e.g., database drivers, serializers). For .NET 8, `System.Ulid` support is adequate.
- Not directly compatible with GUID-based systems without conversion. This is acceptable — the service is greenfield and has no GUID legacy.

## Alternatives Considered

| Alternative                                   | Why rejected                                                                                                                                                                                                                                                                                                                       |
|-----------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **GUID v7**                                   | Time-sortable and ubiquitous but longer (36 chars with hyphens), hyphenated by default, and the canonical string form is less URL-friendly. Would introduce a second ID format alongside the ULIDs already chosen for aliases, audit events, and manifests. Consistency across all identifiers is worth more than GUID's ubiquity. |
| **GUID v4**                                   | Not time-sortable. Random order makes downstream manifest diffing harder (consumers must maintain a separate timestamp to detect order). Rejected for the same consistency reasons as GUID v7.                                                                                                                                     |
| **Snowflake / custom**                        | Unnecessary complexity for MVP. Requires a worker-ID coordinator or service-discovery mechanism. ULID is coordination-free within a single process. Custom formats can be adopted later if Snowflake-style features (sharded generation across workers) become necessary.                                                          |
| **Sequential integer**                        | Not globally unique; requires a centralized counter. Violates the "never reissue" rule if the counter wraps or is reset. Not suitable for a distributed or multi-region future.                                                                                                                                                    |
| **Content-addressable (hash of image bytes)** | Ties UID to image content, which violates the requirement that UID survives metadata-only changes. Aliases and metadata updates would change the UID if the image file is re-encoded. Precludes explicit UID assignment and prevents immutability guarantees.                                                                      |
