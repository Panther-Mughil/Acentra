# REQ-009 — Circuit re-authorization (tenant state must expire)

**Risk Level:** Medium — no schema or data-access change, but it closes a real authorization-staleness hole and touches the Blazor circuit lifecycle.

> **Execution order:** after REQ-005 (it depends on pages subscribing to `TenantState.Changed`). Before REQ-006 so the matrix can prove it. Sequential single run.
> **Origin:** REQ-002 isolation audit finding #2 — "the circuit is never re-authorized".

## 1. Exact Feature Request and Clarified Requirements

**The hole:** `TenantState` is seeded once in `TenantCircuitHandler.OnCircuitOpenedAsync` and never checked again. Blazor Server circuits are long-lived and make **no per-interaction HTTP request** — so the middleware's authorization runs once, at circuit open, and then never again for the life of that circuit.

Consequence: if an administrator revokes a user's membership, or suspends the tenant, a user with an **already-open tab keeps full access to that tenant's data** for as long as the tab stays open. `TenantRegistry.FindForUserAsync` is deliberately uncached so revocation takes effect on the next request — but there is no next request.

**Required:** the circuit's tenant authorization must expire on a bounded, configurable interval, and expiry must be **fail-closed** — clear the tenant so tenant pages stop serving data, and drop any cached component rows.

**Owner directive:** maximum production-ready isolation; never compromise it. An authorization decision that never expires does not meet that bar.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Web/Auth/TenantCircuitHandler.cs`, `src/Acentra.Web/Auth/TenantState.cs`, `src/Acentra.Web/Auth/TenantResolutionConstants.cs`, `src/Acentra.Web/Auth/ServiceCollectionExtensions.cs`, `src/Acentra.Web/appsettings*.json`, `tests/Acentra.IntegrationTests/*Circuit*.cs` or `tests/Acentra.UnitTests/*Circuit*.cs`.
- **In-Scope:** periodic re-validation of the circuit's tenant against live membership + tenant status; fail-closed expiry; configurable interval; testable revalidation unit.
- **Out of Scope:** the HTTP middleware and allowlist (REQ-008), inventory pages (REQ-005), the full isolation matrix (REQ-006), `TenantData/`, `Storage/`.

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Web/Auth/TenantCircuitHandler.cs` | Modify | add periodic revalidation + expiry |
| `src/Acentra.Web/Auth/TenantState.cs` | Modify (only if needed) | expose a clear-with-reason path |
| `src/Acentra.Web/Auth/TenantResolutionConstants.cs` | Modify | interval default/key |
| `src/Acentra.Web/appsettings*.json` | Modify | `Tenant:RevalidationInterval` |
| `tests/**` | Add | revalidation + expiry tests |

## 3. Current Architecture / Context

- `TenantCircuitHandler : CircuitHandler` is registered `Scoped` via `AddTenantResolution()`, so each circuit gets its own handler and its own `TenantState`.
- It currently only implements `OnCircuitOpenedAsync`, reading `IHttpContextAccessor.HttpContext.Items[TenantResolutionConstants.HttpContextItemKey]`.
- `TenantState` exposes `Set(...)`, `Clear()` and a `Changed` event; `ITenantContext` resolves to it.
- REQ-005 requires every inventory page to subscribe to `Changed` and drop its cached rows. **This plan depends on that** — clearing `TenantState` is only safe if pages actually re-render empty. Verify that subscription exists before relying on it; if it does not, stop and report.
- `ITenantRegistry.FindForUserAsync(userId, ct)` is uncached and is the correct authority to re-check. `FindBySlugAsync` is uncached as of REQ-008.
- Identity already has `RevalidatingServerAuthenticationStateProvider` for *auth* state; that does **not** cover our *tenant* state. This plan adds the tenant analogue.

## 4. Implementation Requirements & Interfaces

### 4.1 Capture what revalidation needs, at circuit open
At `OnCircuitOpenedAsync`, record:
- the seeded tenant (`TenantId`, `Slug`),
- the **user id** of the principal that opened the circuit.

The user id must come from a circuit-safe source — the Blazor `AuthenticationStateProvider` (circuit-scoped) or the same `HttpContext.Items` path used for the tenant. Do **not** rely on `IHttpContextAccessor` after circuit open; that is precisely the mistake this codebase already avoids.

### 4.2 Interval + revalidation loop
- Add `RevalidationInterval`, bound from configuration `Tenant:RevalidationInterval`, default **5 minutes**, and clamped to a sane minimum (**no zero or negative interval** — a zero interval would disable the guarantee silently).
- Drive it with a `PeriodicTimer` created at circuit open, cancelled at `OnCircuitClosedAsync` and on `OnConnectionDownAsync` as appropriate. Do not leak a timer or a task past circuit close.
- Revalidation must be **sequential and self-overlapping-safe**: if a check is still running, skip rather than stacking checks.

### 4.3 The revalidation decision (fail closed)
```
authenticated principal has no usable id
  -> clear TenantState
tenant no longer resolvable (unknown / suspended / deleted)
  -> clear TenantState
caller is no longer a member of the circuit's tenant
  -> clear TenantState
otherwise
  -> leave TenantState untouched (do not reassign, do not re-raise Changed)
```
- On any failure: call `state.Clear()` so `IsResolved == false` and `Changed` fires, causing tenant pages to drop cached rows and render the "select a tenant" state.
- **Never** silently substitute a different tenant the user *is* still a member of. Clear, do not switch.
- Log at `Warning` with the tenant slug and reason. Never surface detail to the client.

### 4.4 Testable seam
The timer is hard to test; the decision must not be. Extract the decision into a plain, awaitable method, e.g.:
```csharp
public async Task<bool> RevalidateAsync(CancellationToken ct)   // false == access removed
```
so tests can drive it directly with a fake `ITenantRegistry`, with no timer and no real circuit.

## 5. Step-by-Step Implementation Plan

1. Capture user id + tenant at circuit open.
2. Extract `RevalidateAsync` with the fail-closed decision table of §4.3.
3. Add the `PeriodicTimer` loop, started at circuit open, cancelled at close, with overlap protection.
4. Bind + clamp `Tenant:RevalidationInterval` (default 5 min).
5. Add the config keys to `appsettings.json` / `appsettings.Development.json`.
6. Tests: drive `RevalidateAsync` directly for all four outcomes, plus a test that the interval is clamped for a zero/negative configuration value.
7. Run the full validation set.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; all existing tests still pass.
- [ ] `RevalidateAsync` returns `false` **and** `TenantState.IsResolved` becomes `false` when the user's membership has been revoked.
- [ ] `RevalidateAsync` returns `false` **and** clears state when the tenant has become suspended or unknown.
- [ ] `RevalidateAsync` returns `false` and clears state when the principal has no usable id.
- [ ] `RevalidateAsync` returns `true` and leaves state (and `Changed` event count) untouched when membership and tenant are still valid — **no spurious `Changed` events**, which would needlessly blow away the UI.
- [ ] A revoked tenant is **never** replaced by another tenant the user still belongs to.
- [ ] A zero or negative configured interval is clamped to a positive minimum (asserted by test).
- [ ] The timer is cancelled at circuit close; no task or timer outlives the circuit (assert no background task remains after close).
- [ ] `TenantState.Clear()` fires `Changed` exactly once per expiry.

**Required Tests / Demonstrated Behavior:** the four `RevalidateAsync` outcomes and the interval clamp. Timer behaviour is asserted by cancellation/leak test rather than by waiting on wall-clock time.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- Fail closed: expiry clears, never switches tenant.
- Never read the tenant from `HttpContext` after circuit open.
- `ITenantContext` still reads only from `TenantState`.
- The revalidation interval is the **bound** on staleness and must be stated explicitly in the docs (REQ-007) — do not imply instant revocation.
- No new packages. Do not edit `Program.cs`.

**Non-Goals:** forcing the circuit to close or signing the user out (Identity's revalidating provider owns auth sign-out); pushing revocation over SignalR; per-request tenant checks inside components; distributed cache invalidation across servers.

## 8. Edge Cases and Failure Behavior

- Control-plane database unreachable during revalidation → **fail closed** (clear) and log; never treat an error as "still valid".
- Circuit opened with no seeded tenant (allowlisted page) → nothing to revalidate; do not behave as if access was revoked.
- Revalidation racing circuit close → cancellation must not throw or log an error.
- Multiple rapid reconnections → one loop per circuit; no duplication.
- A user who is still a member but whose tenant changed status transiently → not applicable; status is read fresh each time.

## 9. Existing Behavior That Must Remain Unchanged

- Circuit open still seeds `TenantState` from `HttpContext.Items` exactly as REQ-002 built it.
- `.pi-lens`/LSP diagnostics remain ignorable; `dotnet build` is authoritative.
- REQ-002/REQ-008 middleware semantics and status codes are untouched.
- Existing 36+ tests continue to pass.

## 10. Dependencies and Assumptions

- REQ-005 landed and inventory pages subscribe to `TenantState.Changed`. **If they do not, this plan must stop and report** — clearing the tenant without pages reacting would leave a stale UI, which is exactly the leak architecture §5.6 warns about.
- `ITenantRegistry` is uncached (REQ-008), so each revalidation reads live state.
- Blazor Server's `CircuitHandler` lifetime is per circuit, so a handler-owned timer is circuit-scoped by construction.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none.
**Deferred by the owner:** nothing. The default interval (5 minutes) is specified here; if a different bound is wanted, it is a configuration change, not a code change.

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet test tests/Acentra.UnitTests/Acentra.UnitTests.csproj --logger "console;verbosity=detailed"
```

## 13. Expected Final State

A circuit's tenant authorization expires on a bounded, configurable interval. Revoking membership or suspending a tenant clears the open circuit's tenant state, and tenant pages stop serving that tenant's data without requiring a page reload or a new HTTP request. The staleness bound is documented, not implied.

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-009`.
- Final commit: `feat(tenancy): expire circuit tenant authorization on a bounded interval`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2.
2. Do not use a zero/negative interval as a way to "disable" revalidation — clamp it and keep the guarantee.
3. Fail closed on every ambiguous outcome, including registry errors.
4. Never switch a circuit to a different tenant; clear it.
5. Ignore pi-lens/LSP diagnostics.
6. Do NOT commit; the Overseer handles git.
