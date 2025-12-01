# ADR-0002: Alias model, reservations, and lifecycle

**Status:** Accepted

## Context

VISION.md sections 4, 5, and 12 require the service to support multiple aliases per emoji, primary-alias designation, alias retirement, reserved names, reserved prefixes, protected aliases, and emoji lifecycle states. These concepts are tightly coupled — alias state depends on emoji lifecycle, reservations interact with alias multiplicity rules, and protection policies govern who can modify what.

We need a single coherent model that covers alias multiplicity, alias state, name reservation, protection levels, and emoji lifecycle — plus a decision on where reservation/protection policy is stored for MVP.

## Decision

### 1. Alias model

An alias is a **managed record**, not an incidental string. It is modeled as a separate entity with its own identity, state, and lifecycle.

```
Alias
├── Id          (ULID, system-assigned, stable)
├── Name        (string, unique among active aliases)
├── EmojiId     (ULID? — nullable; null when reserved/unmapped)
├── IsPrimary   (bool — exactly one per emoji with active aliases)
├── State       (enum: Active | Retired | Blocked)
├── Protection  (enum: Normal | Protected)
├── CreatedAt   (DateTime)
└── UpdatedAt   (DateTime)
```

| Field        | Purpose                                                                 |
|--------------|-------------------------------------------------------------------------|
| `Id`         | Stable unique identifier for the alias record itself                    |
| `Name`       | The alias shortcode (e.g., `approved`, `shipit`)                        |
| `EmojiId`    | Which emoji this alias resolves to; `null` when reserved but unassigned |
| `IsPrimary`  | Designates the canonical alias for the linked emoji                     |
| `State`      | Governs whether the alias is usable, retired, or forbidden              |
| `Protection` | Whether the alias requires elevated permission to modify                |

### 2. Alias multiplicity rules

- An emoji has **exactly one primary alias** when it has any active aliases.
- An emoji may have **zero or more secondary aliases**.
- The same alias name **must not be active for more than one emoji** (no duplicate active aliases globally).
- A retired alias preserves its name; the name cannot be reused while the alias is retired.
- A blocked alias name cannot be assigned to any emoji.

### 3. Alias state machine

```
                  ┌──────────┐
        ┌────────▷│  Active   │◁────────┐
        │         └─────┬─────┘         │
        │               │               │
     reactivate     retire          unblock
        │               │               │
        │         ┌─────▼─────┐   ┌─────┴─────┐
        │         │  Retired  │   │  Blocked   │
        │         └───────────┘   └───────────┘
        │                              ▲
        └──────────────────────────────┘
                    block
```

| Transition   | From    | To      | Constraint                                    |
|--------------|---------|---------|-----------------------------------------------|
| `retire`     | Active  | Retired | Alias name is preserved; cannot be reassigned |
| `reactivate` | Retired | Active  | May be remapped to a different emoji          |
| `block`      | Active  | Blocked | Admin only; name becomes permanently unusable |
| `unblock`    | Blocked | Active  | Admin only; restores the alias                |

- A **retired** alias keeps its name reserved and cannot be re-created by a new upload.
- A **blocked** alias is a policy-level prohibition — the name is toxic, inappropriate, or reserved by the system. It cannot be reactivated without admin unblock.
- Alias state transitions are **independent of emoji state** (retiring an alias does not change the emoji).

### 4. Reservation model

Reservations are policy rules that govern which alias names can be created, and by whom.

| Reservation type    | Scope                                | Behavior                                                                                          | Example                                         |
|---------------------|--------------------------------------|---------------------------------------------------------------------------------------------------|-------------------------------------------------|
| **Reserved alias**  | Exact name match                     | The name is set aside. An authorized user can claim it.                                           | `approved` reserved for a future official emoji |
| **Reserved prefix** | Prefix match (e.g., `team-foo-*`)    | Any alias starting with the prefix is reserved. Used for team/org namespaces.                     | `acme-*` reserved for Acme Corp emoji           |
| **Blocked alias**   | Exact name match                     | The name is forbidden. No one can create it.                                                      | `admin` blocked to prevent impersonation        |
| **Protected alias** | Exact name match (on existing alias) | The alias exists and is active, but modifying/retiring/remapping it requires elevated permission. | `approved` is the canonical approval emoji      |

Distinctions:
- **Reserved** = "this name is set aside; ask permission to use it." An authorized user can claim a reserved alias.
- **Blocked** = "this name must never be used." No claim path exists.
- **Protected** = "this alias is in use, but modifying it requires elevated action." It is active but guarded.

Reserved prefixes enable team-owned namespaces without requiring individual alias reservations for every possible name under that prefix.

### 5. Emoji lifecycle

An emoji has a lifecycle beyond simply "present" or "absent."

```
                  ┌──────────┐
        ┌--------▷│  Pending  │
        │         └─────┬─────┘
        │               │
        │         approve    reject
        │               │
        │         ┌─────▼─────┐
        │         │  Active   │──────────────┐
        │         └──┬───┬───┘              │
        │            │   │                  │
        │     promote│   │deprecate    disable
        │            │   │                  │
        │    ┌───────▼─┐ │          ┌───────▼──────┐
        │    │Deprecated│ │          │   Disabled   │
        │    └──┬───┬───┘ │          └───┬─────┬────┘
        │       │   │     │              │     │
        │       │   └─────┼──────────────┘     │
        │       │   disable│                    │
        │       │         │              remove │
        │       │   ┌─────▼─────┐      ┌───────▼──────┐
        │       │   │ Disabled  │      │   Removed    │
        │       │   └───────────┘      └──────────────┘
        │       │                           (terminal)
        │       │
        └───────┘
          promote
```

| State          | Meaning                                                            |
|----------------|--------------------------------------------------------------------|
| **Pending**    | Uploaded, not yet approved for use. Not resolvable by consumers.   |
| **Active**     | Available for use. Resolvable by consumers.                        |
| **Deprecated** | Still usable but flagged for retirement. Consumers should migrate. |
| **Disabled**   | Hidden from normal resolution. Preserved for history/audit.        |
| **Removed**    | Permanently deleted. Terminal state.                               |

| Transition  | From       | To         | Notes                          |
|-------------|------------|------------|--------------------------------|
| `approve`   | Pending    | Active     | Standard activation            |
| `reject`    | Pending    | Removed    | Rejected uploads are removed   |
| `deprecate` | Active     | Deprecated | Mark as superseded             |
| `disable`   | Active     | Disabled   | Temporary removal              |
| `disable`   | Deprecated | Disabled   | Escalate deprecation to hidden |
| `promote`   | Deprecated | Active     | Re-promote if still relevant   |
| `enable`    | Disabled   | Active     | Restore a disabled emoji       |
| `remove`    | Disabled   | Removed    | Permanent deletion             |

- `Removed` is terminal. Removed emoji cannot be restored through the normal workflow (admin recovery is a future concern).
- Aliases of a disabled emoji remain in their current state but do not resolve for consumers.
- Aliases of a deprecated emoji continue to resolve but consumers receive a deprecation signal.

### 6. Policy storage location

For MVP, reservation and protection policies are stored in a **YAML configuration file** shipped with the service, read by a small policy service at startup.

**File location:** `src/EmojiService/EmojiService.Api/Configuration/alias-policy.yaml`

**Policy service:** `AliasPolicyService` — reads the config file, exposes methods like `IsReserved(name)`, `IsBlocked(name)`, `IsProtected(aliasId)`, `GetReservedPrefixes()`.

**Rationale:**
- File-based config is boring, auditable, and version-controlled.
- No database dependency for policy enforcement — policies load with the application.
- Config changes go through the normal PR → deploy cycle, which is appropriate for governance rules.
- Database-backed policy (DynamoDB-stored rules with a management UI) is a **later refinement**, not MVP. The policy service interface is designed so the storage backend can be swapped without changing callers.

## Consequences

**Positive:**
- Single coherent model replaces ad-hoc name handling.
- Alias as a managed record enables audit trails, remapping, and governance.
- Reservation model covers the full range from "set aside" to "forbidden" plus team namespaces via prefixes.
- Emoji lifecycle states give downstream consumers clear signals (deprecated vs disabled vs removed).
- File-based policy is simple to implement, review, and deploy.

**Negative:**
- Alias as a separate entity adds complexity relative to storing aliases as a string array on the emoji record. This is intentional — aliases are managed assets, not incidental strings.
- The alias state machine and emoji lifecycle are independent; callers must check both (e.g., an emoji may be disabled while its aliases remain active). Downstream propagation must handle this.
- File-based policy requires a deploy to change reservation rules. Acceptable for MVP; a runtime management path can be added later.

## Alternatives Considered

| Alternative                                         | Why rejected                                                                                                                                                 |
|-----------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Store aliases as `List<string>` on the emoji record | No alias identity, no audit trail, no remapping, no reservation. Violates VISION requirements.                                                               |
| Single alias per emoji (no secondary aliases)       | Contradicts VISION section 4 requirement for multiple aliases.                                                                                               |
| Combine reservation and blocked into one concept    | They serve different governance needs: reserved = claimable, blocked = forbidden. Conflating them loses the claim path.                                      |
| Database-backed policy from day one                 | Adds infrastructure complexity before the policy model is validated. File-based is simpler for MVP and the policy service interface enables later migration. |
| Hardcode reservations in code (constants)           | Not configurable without a rebuild. YAML file allows policy changes without recompilation.                                                                   |
