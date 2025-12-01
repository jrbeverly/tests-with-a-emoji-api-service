# Architecture Decision Records

Architecture Decision Records (ADRs) capture significant technical choices with their context, rationale, and consequences. They are the project's memory of *why* the system is built the way it is.

## When to write an ADR

Write an ADR when:
- Choosing between two or more meaningful technical approaches.
- Adopting a technology, library, or pattern with cross-cutting impact.
- Changing or reversing a prior architectural decision.

Skip an ADR for bug fixes, routine features that follow existing patterns, and trivial implementation details.

## File naming convention

```
ADR-NNNN-kebab-case-title.md
```

- **NNNN**: Sequential number, zero-padded to 4 digits (0001, 0002, ...).
- **kebab-case-title**: Short imperative phrase (e.g., `use-dynamodb-for-emoji-store`).

Numbers are never reused. A superseded ADR stays in the directory with its status updated.

Examples:
- `ADR-0001-record-architecture-decisions.md`
- `ADR-0002-use-dynamodb-for-emoji-store.md`
- `ADR-0003-propagation-via-eventbridge.md`

## How to supersede a decision

1. Change the old ADR's **Status** to `Superseded by ADR-NNNN`.
2. Write a new ADR explaining what changed and why.
3. Both files remain — the history is preserved.

## Template

Start from [ADR-TEMPLATE.md](ADR-TEMPLATE.md). Every ADR must include: Context, Decision, Status, Consequences, Alternatives considered.

## Index

| ADR                                                                 | Title                                       | Status   |
|---------------------------------------------------------------------|---------------------------------------------|----------|
| [ADR-0001](ADR-0001-record-architecture-decisions.md)               | Record architecture decisions               | Accepted |
| [ADR-0002](ADR-0002-alias-model-reservations-and-lifecycle.md)      | Alias model, reservations, and lifecycle    | Accepted |
| [ADR-0003](ADR-0003-audit-log-schema-and-storage.md)                | Audit log schema and storage                | Accepted |
| [ADR-0004](ADR-0004-batch-upload-manifest-and-validation-model.md)  | Batch upload manifest and validation model  | Accepted |
| [ADR-0005](ADR-0005-emoji-uid-generation-strategy.md)               | Emoji UID generation strategy               | Accepted |
| [ADR-0006](ADR-0006-dynamodb-single-table-key-schema-v1.md)         | DynamoDB single-table key schema v1         | Accepted |
| [ADR-0007](ADR-0007-search-and-discovery-strategy-for-mvp.md)       | Search and discovery strategy for MVP       | Accepted |
| [ADR-0008](ADR-0008-local-asset-storage-and-configuration-model.md) | Local asset storage and configuration model | Accepted |
| [ADR-0009](ADR-0009-propagation-model-and-sync-triggers.md)         | Propagation model and sync triggers         | Accepted |
| [ADR-0010](ADR-0010-consumer-registration-schema.md)                | Consumer registration schema                | Accepted |
