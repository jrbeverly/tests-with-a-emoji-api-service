# ADR-0007: Search and discovery strategy for MVP

**Status:** Accepted

## Context

VISION.md section 6 requires the service to make emoji searchable by meaningful metadata: display name, description, tags, categories, owner, uploader, status, alias, and creation/update information.

ADR-0006 (DynamoDB single-table key schema v1) deliberately deferred search/discovery access patterns — the table has one GSI (GSI1) currently used for alias reverse lookup and global audit listing. The schema reserves flexibility for additional GSIs but does not define them.

The MVP must choose a search strategy that:

- Is simple to implement and test.
- Works for solo-founder scale (hundreds to low thousands of emoji).
- Can be replaced later (e.g., with OpenSearch) without rewriting consumers.
- Avoids adding fixed infrastructure cost.

## Decision

### 1. In-memory index for v1

The v1 search implementation uses an **in-memory index** backed by an `IEmojiIndex` interface. On startup, the index is built from a full `Scan` of all emoji records in the DynamoDB table. After startup, the index is kept current by updating it synchronously on every emoji write (create, update, delete, lifecycle transition).

The index lives in the API process memory. On Lambda, this means it is rebuilt on cold starts (acceptable at MVP scale — a `Scan` of hundreds of records completes in tens of milliseconds). On warm starts, the index is already resident.

```
                    ┌──────────────────────┐
                    │     IEmojiIndex       │  ← replaceable interface
                    │                      │
                    │  Search(SearchRequest)│
                    │  Upsert(Emoji)        │
                    │  Remove(uid)          │
                    └──────────┬───────────┘
                               │
                    ┌──────────▼───────────┐
                    │  InMemoryEmojiIndex   │  ← v1 implementation
                    │                      │
                    │  _byTag: Dict<K,Set> │
                    │  _byCategory          │
                    │  _byOwner             │
                    │  _byStatus            │
                    │  _text: prefix trie   │
                    └──────────────────────┘
```

**Startup path:**

1. `Scan` the table with filter `EntityType = Emoji`.
2. Deserialize each emoji's `Data` attribute.
3. Index by tag, category, owner, status, and free-text terms (primary alias, display name, description).
4. Only `Active` and `Deprecated` emoji are indexed; `Pending`, `Disabled`, and `Removed` are excluded.

**Write path:**

1. After a successful DynamoDB write (put, update, delete, lifecycle change), call `index.Upsert(emoji)` or `index.Remove(uid)`.
2. If the write succeeds but the index update fails (unlikely in-process), the index is stale until the next cold start. Acceptable for MVP — the blast radius is a single Lambda instance.

### 2. Single search endpoint

All search goes through one endpoint:

```
GET /emoji
```

**Query parameters:**

| Parameter  | Type   | Description                                                                  |
|------------|--------|------------------------------------------------------------------------------|
| `q`        | string | Free-text prefix search across primary alias, display name, and description  |
| `tag`      | string | Filter by exact tag match. Repeatable (`?tag=funny&tag=reaction`)            |
| `category` | string | Filter by exact category match. Repeatable.                                  |
| `owner`    | string | Filter by exact owner match                                                  |
| `status`   | string | Filter by emoji lifecycle state (`active`, `deprecated`). Default: `active`. |
| `cursor`   | string | Opaque pagination cursor (ULID of the last returned emoji)                   |
| `limit`    | int    | Max results per page. Default 20, max 100.                                   |

Multiple filter parameters combine with AND semantics. No OR or NOT operators in v1.

**Response:**

```json
{
  "items": [
    {
      "uid": "01jabc123xyz4567890abcdef",
      "primary_alias": "approved",
      "display_name": "Approved Stamp",
      "status": "active",
      "tags": ["reaction", "workflow"],
      "categories": ["approvals"],
      "owner": "eng-team",
      "created_at": "2026-06-01T12:00:00Z"
    }
  ],
  "cursor": "01jabc999zzz1111222333444",
  "has_more": true
}
```

The response returns a **search projection** — not the full emoji domain model. Callers that need the complete record use `GET /emoji/{uid}`.

### 3. Cursor-based pagination

Pagination uses a **cursor** derived from the emoji ULID. ULIDs are time-sortable, so the cursor naturally orders results by creation time (newest first by default).

- The cursor is opaque to the client.
- `has_more: true` means there are more results; the client passes `cursor` from the response as the query parameter for the next page.
- When no cursor is provided, results start from the newest matching emoji.
- Cursor-based pagination avoids the duplicate/missing-item problem of offset-based pagination when data changes between requests.

### 4. Interface abstraction for future replacement

The index is hidden behind a C# interface:

```csharp
public interface IEmojiIndex
{
    SearchResult Search(SearchRequest request);
    void Upsert(Emoji emoji);
    void Remove(string uid);
}
```

`InMemoryEmojiIndex : IEmojiIndex` is the v1 implementation. When the service outgrows it, an `OpenSearchEmojiIndex : IEmojiIndex` can be swapped in without changing callers — only the DI registration changes.

### 5. No DynamoDB schema changes

This ADR adds no new GSIs, no new key prefixes, and no new entity types to the ADR-0006 schema. The in-memory index is purely an application-layer concern. The DynamoDB table and its single GSI remain exactly as defined in ADR-0006.

## Consequences

**Positive:**

- Zero additional infrastructure cost. No new DynamoDB GSIs, no OpenSearch cluster. The index lives in the same Lambda process memory.
- Trivially testable — the in-memory index has no external dependencies. Tests create an index, upsert emoji records, and assert search results.
- Replaceable by design — `IEmojiIndex` is the only touchpoint. Migrating to OpenSearch changes one DI registration and one implementation class.
- Simple implementation — the entire index is roughly 150 lines of C# (dictionaries grouped by metadata field, plus a prefix trie for free-text).
- Warm Lambda containers keep the index resident between invocations. A cold start rebuild is a single `Scan` (tens of milliseconds at MVP scale).

**Negative:**

- Full table `Scan` on every cold start. At MVP scale (hundreds to low thousands of emoji), this is a single-digit to low-double-digit millisecond operation. If the emoji count reaches tens of thousands, cold start latency will degrade — this is the signal to migrate to OpenSearch.
- Index is per-process — each Lambda instance holds its own copy. If two instances serve requests between index updates, they may return slightly different results for a brief window. Acceptable for a search feature (not a transactional read).
- Memory usage grows linearly with emoji count. At 10,000 emoji with roughly 1 KB of indexed metadata each, the index uses approximately 10 MB — well within Lambda's memory limits.
- No full-text search (tokenization, stemming, relevance scoring). The `q` parameter does prefix matching only. This is sufficient for emoji discovery at MVP scale; richer text search comes with OpenSearch.

## Alternatives Considered

| Alternative                                       | Why rejected                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
|---------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **DynamoDB GSIs on tag, category, owner**         | Each searchable field needs its own GSI partition key. Querying across fields (e.g., tag=food AND category=slack) requires N queries plus client-side intersection, or a composite key that cannot cover all combinations. Adding or changing metadata fields requires GSI recreation. Each GSI adds write cost (~$0.25 per million write requests). For MVP, this is over-engineered and introduces ongoing cost for a scale that does not need it.                                                       |
| **DynamoDB GSI with a single `SEARCH` partition** | All emoji share `GSI1PK=SEARCH`, with `GSI1SK` encoding concatenated metadata (e.g., `TAG#food#CAT#slack#OWNER#eng`). Queries use `begins_with` on the composite SK. This works for a few fixed-dimension queries but cannot handle arbitrary filter combinations and creates a hot partition at scale (all emoji under one GSI PK).                                                                                                                                                                       |
| **OpenSearch / Elasticsearch**                    | Full-text search, fuzzy matching, faceted search, relevance scoring. Correct long-term answer for production-scale search. Rejected for v1 because: (a) always-on cost (minimum ~$15–50/month for a single-node cluster), violating the zero-cost-when-idle principle; (b) operational burden (cluster management, version upgrades, snapshot scheduling); (c) the `IEmojiIndex` interface is designed so OpenSearch can be added as a drop-in replacement when the in-memory approach reaches its limits. |
| **Embedded search library (Lucene.NET)**          | Runs in-process, no external cluster. Rejected because it adds a complex dependency (Lucene index files, segment merging, locking) with no clean migration path — replacing it later would mean a different interface and a bigger rewrite. The in-memory index is deliberately trivial so the migration to OpenSearch stays clean.                                                                                                                                                                        |
