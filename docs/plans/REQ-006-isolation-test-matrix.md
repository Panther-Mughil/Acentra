# REQ-006 — Test suite: tenant-isolation matrix, unit tests, end-to-end evidence

**Risk Level:** Medium — no production code, but it is the only thing that turns "we think isolation works" into a proven claim.

> **Execution order:** runs **after** REQ-005 (needs the full stack). Sequential.

## 1. Exact Feature Request and Clarified Requirements

Architecture §5.5 states isolation is a **testable property** and must be tested explicitly. This plan implements that matrix against the real pipeline via `WebApplicationFactory`, plus unit tests for the logic that can be tested cheaply.

The matrix from §5.5, made concrete. **Status codes below reflect REQ-008's hardening** — uniform `403` for every "you cannot use this tenant" cause, and `400` reserved for "no hint supplied at all". Do not assert the pre-REQ-008 codes.

1. Seed tenants A and B with identically named SKUs.
2. Query as A → only A's rows.
3. `PUT` a product with B's id while acting as A → `404`/`403`, never a cross-tenant write.
4. Request tenant B with A's credentials → `403`.
5. Omit the tenant hint → `400`/`403` and zero rows.
6. Switch tenant mid-session → previously loaded rows disappear.
7. Revoke membership or suspend the tenant while a circuit is **open** → that circuit's tenant clears (REQ-009).

## 2. Scope

- **Allowed Files/Directories:** `tests/Acentra.UnitTests/**`, `tests/Acentra.IntegrationTests/**`.
- **In-Scope:** isolation matrix, unit tests for stamping/validation/key building, a test fixture that provisions throwaway tenant databases, JSON endpoints used only by tests if the interactive-only UI blocks `WebApplicationFactory`.
- **Out of Scope:** production code changes, new features, UI polish.

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `tests/Acentra.IntegrationTests/Fixtures/AcentraWebApplicationFactory.cs` | Add | real pipeline + `LocalFileStorage` |
| `tests/Acentra.IntegrationTests/Fixtures/TenantSeedFixture.cs` | Add | tenants A/B + SKU-001 collision |
| `tests/Acentra.IntegrationTests/TenantIsolationTests.cs` | Add | the six matrix cases |
| `tests/Acentra.IntegrationTests/TenantResolutionTests.cs` | Add | 400/403 fail-closed cases |
| `tests/Acentra.IntegrationTests/StorageIsolationTests.cs` | Add | key layout + traversal rejection |
| `tests/Acentra.UnitTests/*` | Add | interceptor, validation, key builder |
| `tests/*/UnitTest1.cs` | Replace | template placeholder |

## 3. Current Architecture / Context

- `WebApplicationFactory<Program>` requires `Program` to be reachable — add `public partial class Program { }` **only if** the minimal-hosting top-level program is not already accessible; if a source change is needed, flag it rather than silently restructuring `Program.cs`.
- Blazor interactive Server components are **not** directly drivable through `WebApplicationFactory` (interaction happens over SignalR). Therefore:
  - Pipeline-level isolation is tested by driving HTTP with the `X-Tenant` header.
  - UI-level switching is tested at the **service/state** level (`TenantState` + `IInventoryService`), and the visual behaviour is verified manually in REQ-005.
  - If a thin test-only JSON endpoint is genuinely required to exercise the write path, add it under an `#if DEBUG`-free but clearly-named `MapGroup("/_test")` guarded to the Testing environment only — and say so in the report.
- Tests must not require MinIO: inject `Provider = "Local"` with a temp root.
- Tests must not touch the developer's real `acentra` database: each run provisions uniquely-named throwaway tenant databases and drops them in teardown.
- Local Postgres must be reachable; if it is not, the suite must **fail loudly** with a clear message rather than silently skipping (a silently skipping isolation test is worse than none).

## 4. Implementation Requirements & Interfaces

**Boundary tests that must exist (the security claims):**

| # | Boundary | Assertion |
| --- | --- | --- |
| B1 | Read isolation | As A, `GET /api/products` returns only A's rows, including A's `SKU-001` and not B's |
| B2 | Cross-tenant write by id | As A, `PUT /api/products/{B's id}` → 404/403; B's row unchanged after |
| B3 | Cross-tenant delete | As A, `DELETE /api/products/{B's id}` → 404/403; B's row still present |
| B4 | Foreign tenant request | A's credentials + `X-Tenant: b` → **uniform 403** (REQ-008) |
| B5 | Missing hint | No `X-Tenant`, authenticated, tenant-scoped path → **400** |
| B6 | Unknown tenant | `X-Tenant: nope`, authenticated → **uniform 403**, byte-identical to B4 and B6b |
| B6b | Suspended tenant | `X-Tenant: <suspended>`, authenticated → byte-identical body to B4/B6 |
| B6c | Unauthenticated probing | Any slug, any hint, unauthenticated → one **byte-identical** response; no tenant lookup performed |
| B7 | Write-path stamping | Insert with empty `TenantId` → stamped; insert with a foreign `TenantId` → throws |
| B8 | Filter + physical split | Querying A's database cannot return B's rows even if the tenant id is forced |
| B9 | Storage isolation | Key from tenant A cannot be opened while acting as B; `..` rejected |
| B10 | `IgnoreQueryFilters` audit | A test/static scan asserts no production call site of `IgnoreQueryFilters()` |
| B11 | No enumeration oracle | Bodies for unknown vs suspended vs non-member are compared byte-for-byte, and never contain the slug |
| B12 | Authorization expiry | Revoked membership / suspended tenant clears an **already-open** circuit's `TenantState` (REQ-009) |
| B13 | Allowlisted path is inert | A foreign/unknown hint on an allowlisted path is ignored — no 403, cookie not cleared |

**Unit tests:** `TenantStampInterceptor` (insert stamp, foreign-id throw, modify/delete throw), `StorageKeyBuilder` (shape, sanitisation, traversal), product validation rules, `TenantDatabaseName` sanitiser.

**Test hygiene:** one fixture per collection, parallelised safely (unique DB names), teardown drops created databases and temp storage roots.

## 5. Step-by-Step Implementation Plan

1. Build the `WebApplicationFactory` fixture with `Local` storage, a temp root, and throwaway tenant database names.
2. Seed tenants A and B, each with `SKU-001`, plus one user who is a member of A only and one member of both.
3. Implement B1–B6 as HTTP-level tests.
4. Implement B7–B9 at the context/service level (faster and more precise than HTTP).
5. Implement B10 as a source scan over `src/` (excluding admin paths, if any are ever added).
6. Replace both `UnitTest1.cs` placeholders with the unit tests in §4.
7. Run the full suite; confirm every case passes and that a deliberately broken filter **fails** the suite (prove the test can actually catch a leak).

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors.
- [ ] `dotnet test Acentra.slnx` → every test passes; **no skipped isolation test**.
- [ ] All of B1–B10 are implemented and named after the boundary they protect.
- [ ] No test depends on MinIO; `Provider = Local` is injected.
- [ ] The suite passes twice in a row (no state bleed between runs).
- [ ] **Mutation check:** temporarily removing one query filter makes at least one test fail; the filter is restored afterwards, with the before/after output reported as evidence.
- [ ] Teardown leaves no leftover throwaway databases (`\l` before/after comparison).

**Required Tests / Demonstrated Behavior:** the B1–B10 table is the test plan; report each row as pass/fail.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- Tests must not weaken or bypass production isolation paths (no test-only bypass of the middleware/filters).
- No production behaviour change to make a test pass. If a test uncovers a real leak, **report it** — do not patch production code inside this plan.
- No dependency on network AWS; no commit of generated databases or `storage/` contents.

**Non-Goals:** load/performance testing, penetration testing, fuzzing, UI screenshot tests, CI pipeline setup.

## 8. Edge Cases and Failure Behavior

- Postgres unreachable → fail with an explicit message naming `podman-compose up -d`.
- Database name collision between parallel test runs → GUID-suffixed names.
- Teardown after a failed test → `IDisposable`/`IAsyncLifetime` still drops the database.
- A test that would pass with **zero** rows must assert a positive row count too, so "empty because broken" cannot look like success.

## 9. Existing Behavior That Must Remain Unchanged

- No production code changed except the optional `public partial class Program` accessibility shim and, if strictly required, a Testing-environment-only test endpoint.
- Existing 2 template tests are replaced, not deleted-and-forgotten.

## 10. Dependencies and Assumptions

- REQ-002/003/004/005 have landed and are integrated.
- Local Postgres is up and the dev role can create/drop databases.
- Interactive Blazor cannot be driven over HTTP; UI switching is asserted at state/service level (documented, not hidden).

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none.

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet test tests/Acentra.IntegrationTests/Acentra.IntegrationTests.csproj --logger "console;verbosity=detailed"
podman exec acentra-postgres psql -U acentra -d postgres -c '\l'
```

## 13. Expected Final State

A suite that proves tenant isolation against the real middleware + EF + provisioning stack, refuses to skip, and demonstrably fails when a filter is removed. Requirement #11 moves from "designed" to "verified".

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-006`.
- Final commit: `test(isolation): tenant isolation matrix, unit tests, mutation evidence`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2.
2. Never leave a skipped or ignored isolation test.
3. If a test reveals a real cross-tenant leak, stop and report it with the failing case — do not fix production code here.
4. Include the mutation-check evidence (filter removed → test fails → filter restored) in the final report.
