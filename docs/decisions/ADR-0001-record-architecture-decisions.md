# ADR-0001: Record architecture decisions

**Status:** Accepted

## Context

This repository is built by a solo founder. Design decisions must be rediscoverable after long gaps between work sessions (see `.claude/philosophy/solo-founder.md`). Without a structured record, rationale is lost and future decisions lack context.

We need a lightweight, file-based Architecture Decision Record (ADR) process that is self-contained within the repository, requires no external tooling, and follows the project's explicit-over-implicit philosophy.

## Decision

We will use numbered ADR files in `docs/decisions/` following these rules:

**When to write an ADR:**
- Choosing between two or more meaningful technical approaches.
- Adopting a new technology, library, or pattern that affects multiple files.
- Changing an existing architectural decision.
- Deprecating or reversing a previous decision.

Do NOT write an ADR for: bug fixes, routine feature additions that follow existing patterns, or trivial implementation details.

**How to number:**
- Sequential, zero-padded to 4 digits (ADR-0001, ADR-0002, ...).
- Numbers are never reused. If a decision is reversed, the new ADR supersedes the old one.

**How to supersede:**
- Change the superseded ADR's status to "Superseded by ADR-NNNN".
- The new ADR explains what changed and why.
- Both files remain in the repository as a historical record.

**File naming:**
- `ADR-NNNN-kebab-case-title.md` (e.g., `ADR-0002-use-dynamodb-for-emoji-store.md`).
- The title portion uses the imperative mood and is short enough to scan in a file listing.

**Template:**
- All ADRs follow `ADR-TEMPLATE.md` with sections: Context, Decision, Status, Consequences, Alternatives considered.

## Consequences

**Positive:**
- Every architectural choice has a dated, searchable record with rationale.
- Decisions are discoverable by reading `docs/decisions/` without external tools.
- Superseded decisions remain visible, so future readers understand the full evolution.
- The naming convention is self-documenting — file order matches decision order.

**Negative:**
- Adds a small documentation step to architectural work.
- Requires discipline to write ADRs before or alongside implementation.

## Alternatives Considered

| Alternative                                        | Why rejected                                                                                                  |
|----------------------------------------------------|---------------------------------------------------------------------------------------------------------------|
| Inline code comments for decisions                 | Code comments drift; ADRs are versioned documents that can be searched independently of the code they affect. |
| External tool (e.g., `adr-tools`, Log4brains)      | Adds a dependency. The solo founder operating model prefers zero-tooling, file-based conventions.             |
| Single decisions log (one file with all decisions) | Harder to reference individually. Separate files enable linking and clearer supersession chains.              |
| YAML/JSON front matter with structured metadata    | Adds parsing complexity. Markdown with a Status header is simpler and sufficient.                             |
