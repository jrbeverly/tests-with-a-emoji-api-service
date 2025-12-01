# ADR-0008: Local asset storage and configuration model

**Status:** Accepted

## Context

Emoji are binary assets (images) as well as records (metadata, aliases, lifecycle). The MVP runs locally with DynamoDB Local and the local filesystem — no S3, no cloud storage. Before writing storage adapter code, we must decide the shape of the asset storage abstraction, the on-disk layout, the content addressing strategy, and the configuration model that wires everything together.

These decisions must hold for the local MVP and make an S3-backed replacement possible without touching domain code. The interface must be narrow enough that the local adapter and a future S3 adapter share the same contract, differing only in DI registration.

ADR-0005 defines the emoji UID as a 26-character lowercase ULID. Every emoji already has a stable, immutable, unique identifier before it reaches the asset store. This ADR builds on that identity.

## Decision

### 1. IAssetStore interface

Asset storage is abstracted behind a single C# interface:

```csharp
public interface IAssetStore
{
    Task StoreAsync(string uid, Stream content, string contentType, CancellationToken ct = default);
    Task<AssetStream> GetAsync(string uid, CancellationToken ct = default);
    Task DeleteAsync(string uid, CancellationToken ct = default);
    Task<bool> ExistsAsync(string uid, CancellationToken ct = default);
}

public record AssetStream(Stream Content, string ContentType, long ContentLength);
```

The interface is intentionally minimal:

- **No update method.** Emoji assets are immutable once stored. Replacing an asset means a new emoji record (new UID), per ADR-0005's immutability rule.
- **No list/enumerate method.** The emoji table is the source of truth for which emoji exist. The asset store is purely a content repository, not a registry. Listing assets on disk is an implementation detail that callers never need.
- **No metadata method beyond what `AssetStream` carries.** Content type and length are returned with the stream on `GetAsync`. More detailed metadata (dimensions, hash, format) belongs in the emoji domain model, not the asset store.
- **Cancellation tokens on every method.** Consistent with .NET async patterns and required for Lambda-friendly timeout handling.

**S3 migration path:** Each method maps directly to an S3 operation — `StoreAsync` → `PutObjectAsync`, `GetAsync` → `GetObjectAsync`, `DeleteAsync` → `DeleteObjectAsync`, `ExistsAsync` → `GetObjectMetadataAsync` with a `NotFound` catch. The `AssetStream` record mirrors S3's response shape (stream + metadata). No interface change needed; only the DI registration changes.

### 2. Local filesystem layout

Assets are stored under a configurable base path, with one subdirectory per emoji UID:

```
{basePath}/
└── {uid}/
    └── original.{ext}
```

**Concrete example** (`basePath` = `var/asset-store`):

```
var/asset-store/
└── 01jabc123xyz4567890abcdef/
    └── original.png
```

Rules:

- **One directory per UID.** Leaves room for additional renditions later (e.g., `thumbnail.webp`, `preview-128.png`) without changing the addressing scheme.
- **`original.{ext}`** preserves the source file extension (derived from content type, not the upload filename). This is informative for local tooling (`file` command, image viewers) and costs nothing.
- **Content type mapping:** `image/png` → `.png`, `image/gif` → `.gif`, `image/jpeg` → `.jpg`, `image/svg+xml` → `.svg`.
- **The base path is configurable** via `AssetStore:BasePath` (see section 4). Default is `var/asset-store` relative to the application root.
- **No nesting by prefix or shard.** At MVP scale (hundreds to low thousands of emoji), a flat directory under `basePath` is fine. If the emoji count reaches tens of thousands, sharding by UID prefix (e.g., `var/asset-store/01/ja/bc123...`) can be added without changing the abstraction — the local adapter implementation changes, not the interface.

### 3. Content addressing strategy: UID-only

Assets are addressed solely by their emoji UID. The path is `{basePath}/{uid}/original.{ext}`.

**No content hash in the path or key.** Rationale:

- ULIDs are already globally unique and immutable (ADR-0005). Adding a content hash provides no additional uniqueness guarantee.
- Content hashing requires reading the entire stream before the store path is known, which complicates streaming uploads and doubles the I/O for large batches.
- Deduplication (same image bytes uploaded twice gets the same storage key) is not an MVP concern. Emoji images are small (max 256 KB, per ADR-0004). If two emoji happen to have identical image bytes, storing them twice is a marginal cost (hundreds of KB) and keeps the identity model simple — each emoji UID owns its own storage independently.
- Content-addressable storage would break the immutability contract: if the UID is derived from the hash, then changing any metadata (which doesn't touch the image) would change the UID, violating ADR-0005.

**If deduplication is needed later:** A content-hash index can be added at the domain/application layer (hash → set of UIDs) without changing the asset store interface or on-disk layout. The local adapter does not need to know about it.

### 4. Configuration model

Configuration uses the .NET Options pattern with `appsettings.json` as the backing source. Two option classes:

**AssetStoreOptions:**

```csharp
public class AssetStoreOptions
{
    public const string Section = "AssetStore";

    public string BasePath { get; init; } = "var/asset-store";
}
```

| Key                   | Type   | Default           | Description                                                                                          |
|-----------------------|--------|-------------------|------------------------------------------------------------------------------------------------------|
| `AssetStore:BasePath` | string | `var/asset-store` | Root directory for local asset files. Relative paths are resolved from the application content root. |

**DynamoDbOptions:**

```csharp
public class DynamoDbOptions
{
    public const string Section = "DynamoDB";

    public string ServiceUrl { get; init; } = "http://localhost:8000";
    public string TableName { get; init; } = "emoji-registry";
    public string Region { get; init; } = "us-east-1";
}
```

| Key                   | Type   | Default                 | Description                                                                                                                      |
|-----------------------|--------|-------------------------|----------------------------------------------------------------------------------------------------------------------------------|
| `DynamoDB:ServiceUrl` | string | `http://localhost:8000` | DynamoDB endpoint URL. Defaults to DynamoDB Local for MVP. Set to `https://dynamodb.{region}.amazonaws.com` for production.      |
| `DynamoDB:TableName`  | string | `emoji-registry`        | DynamoDB table name. Single-table design per ADR-0006.                                                                           |
| `DynamoDB:Region`     | string | `us-east-1`             | AWS region. DynamoDB Local ignores this value (it accepts any non-empty region). Used when `ServiceUrl` points to real DynamoDB. |

**Registration in Program.cs:**

```csharp
builder.Services.Configure<AssetStoreOptions>(
    builder.Configuration.GetSection(AssetStoreOptions.Section));

builder.Services.Configure<DynamoDbOptions>(
    builder.Configuration.GetSection(DynamoDbOptions.Section));
```

**`appsettings.json` (committed, non-sensitive defaults):**

```json
{
  "AssetStore": {
    "BasePath": "var/asset-store"
  },
  "DynamoDB": {
    "ServiceUrl": "http://localhost:8000",
    "TableName": "emoji-registry",
    "Region": "us-east-1"
  }
}
```

**`appsettings.Development.json` (committed, local overrides):**

```json
{
  "AssetStore": {
    "BasePath": "var/asset-store"
  },
  "DynamoDB": {
    "ServiceUrl": "http://localhost:8000",
    "TableName": "emoji-registry-dev"
  }
}
```

### 5. DynamoDB Local credentials

DynamoDB Local does not validate credentials — it accepts any non-empty access key and secret. For local development, credentials are provided via the standard AWS SDK credential chain:

**Recommended: environment variables** (set in devcontainer or shell profile):

```bash
export AWS_ACCESS_KEY_ID=fake
export AWS_SECRET_ACCESS_KEY=fake
export AWS_DEFAULT_REGION=us-east-1
```

These are fake but non-empty, which satisfies both the AWS SDK credential resolver and DynamoDB Local's no-op validation. The `AWS_DEFAULT_REGION` environment variable provides a fallback when `DynamoDB:Region` is not explicitly configured, though `DynamoDB:Region` in `appsettings.json` takes precedence.

**Why not `appsettings.json` for credentials:** Credentials are secrets and must never be committed to the repository. Environment variables are the standard AWS SDK mechanism and work identically across local dev, CI, and production — only the values change (fake locally, real IAM credentials in production via instance roles or SSO).

**Configuration precedence (later sources override earlier):**

1. `appsettings.json` (committed defaults)
2. `appsettings.{Environment}.json` (committed environment overrides)
3. Environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_DEFAULT_REGION`)
4. `appsettings.Local.json` (git-ignored, developer-specific overrides)

### 6. Interface lifetime and registration

```csharp
// Local MVP — DI registration
builder.Services.AddSingleton<IAssetStore, LocalAssetStore>();

// Future S3 — swap one line
// builder.Services.AddSingleton<IAssetStore, S3AssetStore>();
```

`IAssetStore` is registered as a **singleton**. The local adapter maintains no mutable state beyond filesystem access. The S3 adapter wraps an `IAmazonS3` client (itself thread-safe and designed for reuse). There is no per-request state, so scoped/transient lifetimes provide no benefit and add DI overhead.

## Consequences

**Positive:**

- The `IAssetStore` interface has four methods, each mapping directly to an S3 operation. Swapping adapters changes one DI registration line.
- UID-only addressing is deterministic: given a UID, the file path is always `{basePath}/{uid}/original.{ext}`. No hash computation, no database lookup, no indirection.
- The Options pattern makes every configurable value type-safe, discoverable via IDE (navigate to `AssetStoreOptions` or `DynamoDbOptions`), and testable via `IOptions<T>` or `Options.Create()`.
- Committed `appsettings.json` files mean the application starts out of the box after `git clone` — no manual config file creation needed.
- Single directory per UID leaves room for additional renditions (thumbnail, preview, different sizes) without restructuring.
- Credentials stay out of committed files entirely. The fake-credential approach for DynamoDB Local is the standard pattern documented in the AWS DynamoDB Local developer guide.

**Negative:**

- UID-only addressing means no automatic deduplication. Uploading the same image bytes for two different emoji stores two copies. At MVP scale (max 256 KB per emoji, hundreds of emoji), the total waste is tens of MB — acceptable. Deduplication can be added at the application layer later.
- Flat directory structure (`{basePath}/{uid}/`) will reach filesystem limits if the emoji count grows to hundreds of thousands. Some filesystems degrade with >10,000 entries in a single directory. Sharding by UID prefix (e.g., `{basePath}/01/ja/{uid}/`) can be added transparently in the local adapter without changing the interface or callers.
- `appsettings.Development.json` is committed, which is unusual in some projects (typically git-ignored). This is intentional: the defaults are non-sensitive and ensure a working local environment on first clone. True secrets (API keys, connection strings with passwords) still go in git-ignored `appsettings.Local.json` or environment variables.
- No `GetInfo` method on `IAssetStore` that returns metadata without opening the stream. This is intentional — the emoji domain record already carries the content type and any relevant metadata. The asset store is purely a content repository. If S3-specific metadata queries are needed later, a separate interface (e.g., `IAssetMetadataStore`) can be added without changing `IAssetStore`.

## Alternatives Considered

| Alternative                                                            | Why rejected                                                                                                                                                                                                                                            |
|------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Content-hash addressing (`{basePath}/{sha256}/{uid}/original.ext`)** | Requires reading the entire stream before the path is known. Doubles I/O for batch uploads. Deduplication is not an MVP concern. Can be added at the application layer later without changing the store.                                                |
| **Content-hash only (no UID in path)**                                 | Ties identity to image bytes. Metadata changes (tags, description) would not be reflected. Violates ADR-0005's immutability model and makes audit trail impossible (audit events reference UIDs, not hashes).                                           |
| **Store assets directly in DynamoDB as binary attributes**             | DynamoDB item size limit is 400 KB. Emoji images up to 256 KB fit, but it couples asset storage to the database, makes S3 migration harder, and increases DynamoDB read/write costs (storage is free in local filesystem, paid in DynamoDB throughput). |
| **`IAssetStore` with update/replace method**                           | Violates emoji immutability (ADR-0005). Replacing an emoji asset means a new UID. An update method would encourage mutable-asset patterns that conflict with the audit model.                                                                           |
| **`IAssetStore` with list/enumerate method**                           | The emoji table is the registry. Adding list to the asset store creates a second source of truth and a consistency risk (files on disk vs records in DB). The asset store should not know which emoji exist.                                            |
| **Hardcoded paths and credentials in code**                            | Not configurable across environments. Violates twelve-factor config principle. Options pattern is the .NET standard and trivial to set up.                                                                                                              |
| **Separate config files for asset store and DynamoDB**                 | Single `appsettings.json` with sections is the standard .NET pattern. Splitting into multiple files adds discoverability cost with no benefit at this scale.                                                                                            |
| **Use AWS SSM Parameter Store or Secrets Manager for local config**    | Adds cloud dependency for local development. Environment variables and `appsettings.json` are sufficient for local + CI. SSM/Secrets Manager can be layered on for production without changing the Options classes.                                     |
| **Credentials in `appsettings.json`**                                  | Security risk. Credentials committed to version control are a leak vector. Environment variables or the AWS SDK credential chain are the standard, safer approach.                                                                                      |
| **Multiple asset store interfaces (one per storage backend)**          | Defeats the purpose of abstraction. A single `IAssetStore` with adapter implementations is simpler, testable (mock one interface), and proven across the .NET ecosystem.                                                                                |
