# REQ-008 — Tenant resolution hardening (anti-enumeration, cache removal, fail-closed allowlist)

**Risk Level:** High — this rewrites the authorization decision path and removes a cache from a security boundary. It changes response semantics that existing tests assert.

> **Execution order:** after REQ-003. Before REQ-005 and REQ-006. Sequential single run.
> **Origin:** findings from the REQ-002 isolation audit. All four decisions below are owner-approved.

## 1. Exact Feature Request and Clarified Requirements

Four approved changes, ordered by importance:

1. **Kill the tenant-enumeration oracle.** Today an unauthenticated caller can tell whether a tenant exists: unknown slug → `400 "Unknown tenant 'x'"`, existing slug → `302`/`403`. The tenant lookup also runs *before* the authentication check, so this leaks to the public internet with no account. Fix: authenticate first, then collapse every "you cannot use this tenant" outcome into one identical `403`.
2. **Remove the 2-minute tenant cache.** `TenantRegistry.FindBySlugAsync` caches the Active-only lookup for `CacheTtl = 2 min`, so a **suspended tenant keeps working for up to 2 minutes**. The same cache also stores `null`, so **a newly created tenant is unresolvable for up to 2 minutes**. Owner decision: **Option A — remove the cache entirely.** Correctness at a security boundary beats a micro-optimisation; the lookup is one indexed row on a unique column.
3. **Tighten the tenant-agnostic allowlist** so it cannot become a hole: remove entries with no backing endpoint, and stop treating directories as tenant-agnostic files.
4. **Make the continuity cookie tamper-resistant and correct**: only clear it when the cookie itself was the failing hint, clear it consistently, and emit `Secure` correctly behind a TLS-terminating proxy.

**Owner directive driving this plan:** *maximum production-ready application, with proper isolation security — never compromise it.* Where a choice trades debuggability or convenience against isolation correctness, isolation wins.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Infrastructure/ControlPlane/TenantRegistry.cs`, `src/Acentra.Infrastructure/ControlPlane/ServiceCollectionExtensions.cs`, `src/Acentra.Web/Middleware/**`, `src/Acentra.Web/Auth/**`, `src/Acentra.Web/Program.cs`, `src/Acentra.Web/appsettings*.json`, `tests/Acentra.IntegrationTests/TenantResolution*.cs`.
- **In-Scope:** the four changes above, plus updating the REQ-002 tests that assert the old status codes.
- **Out of Scope:** `TenantData/**` (REQ-003), `Storage/**` (REQ-004), inventory UI (REQ-005), circuit re-authorization (REQ-009), the isolation matrix proper (REQ-006).

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Infrastructure/ControlPlane/TenantRegistry.cs` | Modify | remove `IMemoryCache`, `CacheTtl`, `CacheKeyPrefix` |
| `src/Acentra.Infrastructure/ControlPlane/ServiceCollectionExtensions.cs` | Modify | drop the now-unused `IMemoryCache` registration if nothing else uses it |
| `src/Acentra.Web/Middleware/TenantResolutionMiddleware.cs` | Rewrite decision path | auth-first ordering + uniform 403 |
| `src/Acentra.Web/Middleware/TenantAgnosticPaths.cs` | Modify | remove `/health`, `/.well-known`; require a real file (not a directory) |
| `src/Acentra.Web/Auth/ITenantResolver.cs` + 4 resolvers | Modify | hint carries its **source** |
| `src/Acentra.Web/Auth/TenantResolutionConstants.cs` | Modify | add the generic denial message |
| `src/Acentra.Web/Program.cs` | Modify | `UseForwardedHeaders` when enabled (must be first) |
| `src/Acentra.Web/appsettings*.json` | Modify | `ForwardedHeaders:Enabled` (default false) |
| `tests/Acentra.IntegrationTests/TenantResolution*.cs` | Modify + add | new semantics; keep all coverage |

## 3. Current Architecture / Context

- REQ-002 shipped `TenantResolutionMiddleware` with this order: read hint → **tenant lookup → 400 if unknown** → auth check → membership check → 403 if not a member.
- The 400 fires **before** authentication, which is what makes the oracle public.
- `ITenantResolver.Resolve(HttpContext)` returns `string?` only — the caller cannot tell whether a hint came from the header, query string, subdomain or the continuity cookie. That is what makes targeted cookie clearing impossible today.
- `TenantAgnosticPaths.IsTenantAgnostic` falls back to `environment.WebRootFileProvider.GetFileInfo(value)` and returns `Exists` — which is **also true for directories**.
- `TenantRegistry.FindForUserAsync` is already correctly uncached. Keep that property.
- The middleware currently does the **opposite** of this plan on allowlisted paths: a bad hint there returns `403` even though the path needs no tenant. That is the crafted-link cookie-clearing vector, and this plan removes it.

## 4. Implementation Requirements & Interfaces

### 4.1 New resolution order (authoritative)

```
1. Read the hint and its SOURCE (header > query > subdomain > cookie).
2. If the request is NOT authenticated:
     requiresTenant ? challenge (401 for /api/*, cookie redirect otherwise) : next()
   -> NO tenant lookup happens. No hint can be probed. No state is set.
3. Authenticated, and no hint supplied:
     requiresTenant ? 400 : next()
4. Authenticated, hint supplied:
     look up the tenant (always, uncached)
     look up the caller's memberships (always, uncached)
     decide:
       tenant resolvable AND caller is a member -> set state, set HttpContext.Items,
                                                   publish cookie, next()
       otherwise:
         requiresTenant ? UNIFORM 403 : next()   // allowlisted paths NEVER fail on a hint
```

Two consequences that are requirements, not side effects:
- **Allowlisted paths never fail because of a hint.** A bad or foreign hint there is simply ignored — no `403`, and the continuity cookie is **not** cleared. This closes the "crafted `?tenant=` forces a 403 and wipes the cookie" vector.
- **No hint on an allowlisted path is normal** (e.g. `/`, `/Account/Login`) and must not fail.

### 4.2 Uniform 403 (the anti-enumeration requirement)

All five causes must produce a **byte-identical** response:

| Cause | Client sees | Server logs |
| --- | --- | --- |
| Slug fails normalization | identical 403 | normalization failure |
| Tenant unknown | identical 403 | unknown slug |
| Tenant exists but not `Active` | identical 403 | suspended slug |
| Caller is not a member | identical 403 | membership miss |
| Principal has no usable id | identical 403 | unusable identity |

- Body: one constant string (`TenantResolutionConstants.AccessDeniedMessage`), **never the slug**, `Content-Type: text/plain; charset=utf-8`, no extra headers that vary by cause.
- **Equalise the work performed:** when authenticated with a hint, always perform the tenant lookup *and* the membership lookup before deciding, so response timing does not discriminate between "unknown tenant" and "not a member". Do not short-circuit the second query when the first returns nothing.
- Log the precise cause at `Warning` with the slug for operators. Debuggability moves to logs, not to the response.

### 4.3 Cache removal

- Delete the `IMemoryCache` dependency, `CacheTtl` and `CacheKeyPrefix` from `TenantRegistry`; query the database on every call.
- No negative caching means a **newly created tenant resolves immediately**.
- A **suspended tenant stops resolving on the next request**.
- If nothing else consumes `IMemoryCache`, remove that registration too; if something does, leave it registered and say so in the report.
- Keep `FindForUserAsync` uncached.

### 4.4 Hint source (interface change)

```csharp
public enum TenantHintSource { None, Header, Query, Subdomain, Cookie }
public readonly record struct TenantHint(string? Value, TenantHintSource Source);
public interface ITenantResolver { TenantHint Resolve(HttpContext context); }
```
Update all four resolvers. Precedence stays: header > query > subdomain > cookie.

Cookie rules:
- **Clear the continuity cookie only when `Source == Cookie`** and resolution/authorization failed. A failure caused by a header/query/subdomain hint must leave the cookie alone.
- Clear it consistently on **every** cookie-sourced failure branch (today the unparseable-slug branch forgets to).

### 4.5 Allowlist tightening

- **Remove `/health` and `/.well-known`** — no endpoint exists. Fail closed; add them back only alongside a real endpoint. Note this in the report as an intentional behaviour change.
- Static-file fallback must require **`file.Exists && !file.IsDirectory`**, so a whole directory tree cannot become tenant-agnostic.
- Keep `MatchesPrefix` as-is — its segment-boundary check is verified correct (`/accounting`, `/libfoo`, `/_blazorXYZ` correctly do not match).

### 4.6 `Secure` cookie behind a proxy

- Add `ForwardedHeaders` support gated on config (`ForwardedHeaders:Enabled`, default `false`). When enabled, call `app.UseForwardedHeaders(...)` as the **first** middleware, honouring `X-Forwarded-Proto`/`X-Forwarded-For` with an explicit `ForwardLimit` and no unknown-proxy trust.
- Without this, `Request.IsHttps` is false behind a TLS-terminating proxy and the continuity cookie is emitted **without `Secure`**.
- Do not enable it by default in Development; document it.

## 5. Step-by-Step Implementation Plan

1. Introduce `TenantHint`/`TenantHintSource`; update `ITenantResolver` and the four resolvers.
2. Rewrite the middleware decision path per §4.1, with the uniform-403 helper and server-side logging per §4.2.
3. Strip the cache from `TenantRegistry` (§4.3) and clean up DI.
4. Tighten `TenantAgnosticPaths` (§4.5).
5. Cookie source-tracking + consistent clearing (§4.4).
6. `UseForwardedHeaders` behind config (§4.6).
7. **Update the REQ-002 tests** that assert the old codes. Do not delete coverage — retarget it to the new semantics and add the new tests in §6.
8. Run the full validation set.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; `dotnet test` all green (existing 36 must still pass after retargeting).
- [ ] **No oracle:** unauthenticated requests with (a) no hint, (b) a valid slug, (c) an unknown slug, (d) a suspended slug all produce a **byte-identical** response (status + body + relevant headers). Asserted by test.
- [ ] **No oracle, authenticated:** unknown vs suspended vs non-member produce **byte-identical** `403` bodies. Asserted by test.
- [ ] The response body **never contains the slug**.
- [ ] Authenticated + no hint + tenant-scoped path → `400`.
- [ ] Unauthenticated + tenant-scoped path → one uniform challenge (`401` for `/api/*`, redirect otherwise), **and no tenant lookup is performed** (assert via a registry spy/counter).
- [ ] A **newly created** tenant resolves on the **immediately next** request (no negative caching).
- [ ] A tenant flipped to **suspended** stops resolving on the **immediately next** request.
- [ ] Allowlisted path + foreign/unknown hint → **not** 403, and the continuity cookie is **not** cleared.
- [ ] Cookie-sourced failure → cookie **is** cleared; header/query-sourced failure → cookie **untouched**.
- [ ] `/health` now requires a tenant (no longer allowlisted).
- [ ] A **directory** under wwwroot is **not** treated as tenant-agnostic.
- [ ] `TenantRegistry` no longer references `IMemoryCache`; `CacheTtl`/`CacheKeyPrefix` are gone.
- [ ] With `ForwardedHeaders:Enabled=true` and `X-Forwarded-Proto: https`, the continuity cookie is emitted with `Secure`.

**Required Tests / Demonstrated Behavior:** the byte-identical assertions are the core deliverable — they are what makes "no oracle" a fact rather than a claim. Compare full response bodies, not just codes.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- Membership remains **authority**; every hint source is re-authorized on every request, including the cookie.
- Never cache a tenant lookup, and never cache a negative result.
- Fail closed: an unresolved tenant never means "all tenants" or a default tenant.
- `ITenantContext` is still never read from `HttpContext` after circuit start.
- Do not change `TenantAgnosticPaths.MatchesPrefix` semantics.

**Non-Goals:** circuit re-authorization (REQ-009); inventory endpoints; rate limiting; CAPTCHA; audit-log storage (logging to the app logger is enough here).

## 8. Edge Cases and Failure Behavior

- Hint present but unparseable (`'ACME!!'`) → treated exactly like unknown (uniform 403, or ignored on allowlisted paths).
- Multiple hints present → header wins; only the winning source may trigger cookie clearing.
- Authenticated on `/_blazor` with a stale cookie → tenant ignored, circuit unseeded, tenant pages show "select a tenant". Never a 403 that would wedge the circuit.
- Authenticated but `NameIdentifier` missing/unparseable → uniform 403 (tenant-scoped) or ignore (allowlisted).
- `Response.HasStarted` before failing → log and return without writing (unchanged behaviour).

## 9. Existing Behavior That Must Remain Unchanged

- Pipeline order in `Program.cs` stays `UseHttpsRedirection` → `UseAuthentication` → tenant middleware → `UseAuthorization` (add `UseForwardedHeaders` *before* `UseHttpsRedirection` only when enabled).
- `AddTenantResolution()` remains the Web-side seam; the two `TenantState`/`ITenantContext` registrations stay verbatim.
- Control-plane schema, Identity, migrations: untouched.
- REQ-003 (`TenantData/`) and REQ-004 (`Storage/`): untouched.

## 10. Dependencies and Assumptions

- REQ-003 has landed; `ITenantContext` resolves to `TenantState`.
- The control-plane database is reachable and seeded with `acme` (active) and `globex` (active).
- Removing the cache is acceptable because the lookup is a single indexed row; no throughput requirement exists that justifies a stale authorization decision.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. Owner approved: uniform 403 with auth-first ordering; **Option A — cache removal**; proxy-aware `Secure` cookie.

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet test tests/Acentra.IntegrationTests/Acentra.IntegrationTests.csproj --logger "console;verbosity=detailed"
```
Runtime spot-check (app hosted, real curl): `X-Tenant: acme` vs `X-Tenant: nonexistent` as an unauthenticated caller must be indistinguishable; `X-Tenant: acme` vs a non-member must be byte-identical when authenticated.

## 13. Expected Final State

An unauthenticated caller can learn **nothing** about which tenants exist. A suspended tenant stops working on the next request. A newly created tenant works immediately. The allowlist can no longer absorb unintended routes or directories. The continuity cookie cannot be cleared by a crafted link. All of it asserted by tests that compare full response bodies.

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-008`.
- Final commit: `fix(tenancy): uniform 403, remove tenant cache, tighten allowlist, source-aware cookie`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2. `TenantData/**` and `Storage/**` belong to other plans — do not touch them.
2. Bytes matter: the uniform-403 test must compare full bodies, not just status codes.
3. Retarget the existing REQ-002 tests to the new semantics — never delete a case to make the suite pass.
4. Ignore pi-lens/LSP diagnostics entirely; `dotnet build` is the only source of truth.
5. Do NOT commit; the Overseer handles git.
