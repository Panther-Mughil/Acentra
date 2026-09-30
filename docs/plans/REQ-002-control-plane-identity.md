# REQ-002 — Control plane: ASP.NET Core Identity, tenant registry, membership, tenant resolution middleware

**Risk Level:** High — authentication + the tenant-identity decision point that every request depends on.

> **Execution order:** runs **in parallel** with REQ-003 and REQ-004, after REQ-001. Owns `src/Acentra.Infrastructure/ControlPlane/**` and `src/Acentra.Web/Middleware/**` + `src/Acentra.Web/Auth/**` only.

## 1. Exact Feature Request and Clarified Requirements

Give the platform an identity and a tenant registry that are *not* themselves tenant-scoped:

- **ASP.NET Core Identity** as the auth provider (decision #1). Identity tables live in the **control-plane database**, never in a tenant database.
- A **tenant registry** (slug → tenant + its database name) and **memberships** (which user belongs to which tenant, with a role).
- A `TenantResolutionMiddleware` that turns a request hint (`X-Tenant` header by default, subdomain as an alternative) into a resolved, **authorized** tenant, published on a scoped `ITenantContext`.
- Blazor Server forces one extra thing (§4.1 of the architecture doc): the resolved tenant must be pinned into a **circuit-scoped, mutable `TenantState`** so the UI switcher can change it without a new HTTP request.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Infrastructure/ControlPlane/**`, `src/Acentra.Web/Middleware/**`, `src/Acentra.Web/Auth/**`, `src/Acentra.Web/Components/Pages/Account/**`, `src/Acentra.Web/appsettings*.json`.
- **In-Scope:** `ControlPlaneDbContext`, Identity setup, `AppUser`, `Tenant`, `TenantMembership`, `ITenantRegistry`, `ITenantResolver` + header/subdomain resolvers, `TenantResolutionMiddleware`, circuit-scoped `TenantState`, control-plane migrations, login/logout/register pages.
- **Out of Scope:** tenant-database provisioning (REQ-003), storage (REQ-004), inventory UI (REQ-005), tenant-isolation tests (REQ-006).

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Infrastructure/ControlPlane/ControlPlaneDbContext.cs` | Add | Identity + registry context |
| `src/Acentra.Infrastructure/ControlPlane/Configurations/*.cs` | Add | `Tenant`, `TenantMembership` mapping |
| `src/Acentra.Infrastructure/ControlPlane/AppUser.cs` | Add | `IdentityUser<Guid>` |
| `src/Acentra.Infrastructure/ControlPlane/TenantRegistry.cs` | Add | implements `ITenantRegistry` |
| `src/Acentra.Infrastructure/ControlPlane/ServiceCollectionExtensions.cs` | Complete stub | `AddControlPlane` |
| `src/Acentra.Infrastructure/Migrations/ControlPlane/**` | Add | `dotnet ef migrations add` output |
| `src/Acentra.Web/Auth/TenantState.cs` | Add | circuit-scoped mutable tenant |
| `src/Acentra.Web/Auth/*TenantResolver.cs` | Add | header + subdomain strategies |
| `src/Acentra.Web/Middleware/TenantResolutionMiddleware.cs` | Add | resolves + authorizes + publishes |
| `src/Acentra.Web/Components/Pages/Account/*.razor` | Add | login / logout / register |

## 3. Current Architecture / Context

- REQ-001 provides `ITenantContext`, `TenantDescriptor`, `MembershipRole`, `Tenant`, `TenantMembership`, and the stub `AddControlPlane`.
- `Program.cs` (REQ-001) already calls `UseAuthentication()` **before** `TenantResolutionMiddleware` and `UseAuthorization()` **after** — resolution must be able to see `HttpContext.User`.
- PostgreSQL 17 runs locally via `podman-compose`; `MinIO` is added in the same compose file by REQ-001/REQ-004.

## 4. Implementation Requirements & Interfaces

**Two databases, one control plane.** `ControlPlaneDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>` with `DbSet<Tenant>` and `DbSet<TenantMembership>`; connection string `ConnectionStrings:ControlPlane`.

```csharp
public interface ITenantRegistry {
    Task<TenantDescriptor?> FindBySlugAsync(string slug, CancellationToken ct);
    Task<IReadOnlyList<TenantDescriptor>> FindForUserAsync(Guid userId, CancellationToken ct);
}
```

- `Tenant.Slug` unique, lower-case, `^[a-z0-9-]{2,40}$`.
- `TenantMembership` unique on `(TenantId, UserId)`.
- Registry reads are cached in `IMemoryCache` by slug, ~2 min TTL.

**Resolution flow (must be implemented in this order):**

1. Read the hint — `X-Tenant` header, else the first subdomain label when the host has ≥3 labels.
2. Look the slug up via `ITenantRegistry`. Unknown → **stop, 400**.
3. **Authorize**: the authenticated principal must have a `TenantMembership` for that tenant → otherwise **403**.
4. Publish a scoped `ITenantContext` (`TenantId`, `Slug`, `IsResolved = true`).
5. Copy it into the **circuit-scoped `TenantState`** when a circuit starts.

**Fail-closed rules (non-negotiable):**
- Unresolved tenant must **never** mean "all tenants" or "no filter applied".
- No hint on a request in the current tenant flow → 400, not a default tenant.
- The client-supplied slug is a **hint only**; membership is authority.

**`TenantState` (circuit-scoped, mutable):**
```csharp
public sealed class TenantState : ITenantContext {
    public Guid TenantId { get; private set; }
    public string Slug { get; private set; } = string.Empty;
    public bool IsResolved => TenantId != Guid.Empty;
    public event Action? Changed;
    public void Set(Guid id, string slug);
    public void Clear();
}
```
Registered `Scoped`. The switcher calls `Set()`; subscribers (pages) drop cached rows when `Changed` fires. `ITenantContext` resolves to `TenantState`, and **never** reads `HttpContext`.

**Identity:** `AddIdentityCore<AppUser>()` + cookies (`AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()`), `AddRoles<IdentityRole<Guid>>()`, `AddEntityFrameworkStores<ControlPlaneDbContext>()`. Login/register pages use **static SSR** render mode (Identity flows need real HTTP round-trips); everything else stays interactive.

**Control-plane migrations:** `dotnet ef migrations add InitialControlPlane --context ControlPlaneDbContext -o Migrations/ControlPlane -p src/Acentra.Infrastructure -s src/Acentra.Web`. Dev startup applies them; two seed tenants (`acme`, `globex`) with one seeded demo user as a member of both, so the switcher has something to switch between.

## 5. Step-by-Step Implementation Plan

1. `AppUser`, `ControlPlaneDbContext`, entity configurations, `ITenantRegistry` + `TenantRegistry` (with `IMemoryCache`).
2. Complete `AddControlPlane`: context (`UseNpgsql` with `MigrationsAssembly("Acentra.Infrastructure")`), Identity, cookies, registry, cache.
3. Add `TenantState`, `ITenantResolver`, `HeaderTenantResolver`, `SubdomainTenantResolver`, `TenantResolutionMiddleware`; register in `AddControlPlane` + Web DI.
4. Generate + apply control-plane migrations; add the dev seed (`TenantSeeder`).
5. Add Account pages (login / logout / register) as static SSR.
6. `dotnet build` + `dotnet test`; confirm 0 warnings.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; `dotnet test` → 2/2.
- [ ] `dotnet ef migrations list --context ControlPlaneDbContext` shows `InitialControlPlane`; `dotnet ef database update --context ControlPlaneDbContext` applies it cleanly against local Postgres.
- [ ] `X-Tenant: acme` from a user who is an `acme` member → middleware resolves, `ITenantContext.IsResolved == true`.
- [ ] `X-Tenant: acme` from a non-member → **403**.
- [ ] `X-Tenant: does-not-exist` → **400**.
- [ ] Request with no hint → **400**, and `ITenantContext.IsResolved == false`.
- [ ] `TenantState.Set(...)` from the switcher changes what `ITenantContext` reports mid-circuit (no new HTTP request).
- [ ] Login/register/logout round-trip works with a seeded user.

**Required Tests / Demonstrated Behavior:** resolution + authorization cases above, driven through the real pipeline (REQ-006 formalises the matrix; a focused test here is acceptable).

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- `ITenantContext`/`TenantState` is **Scoped** — never singleton, never static.
- Never read the tenant from `HttpContext` after circuit start.
- Identity tables must not appear in a tenant database.
- Pipeline order from REQ-001 is unchanged.

**Non-Goals:** tenant database provisioning, inventory entities/UI, file storage, RBAC beyond storing the role, password-reset/2FA/email.

## 8. Edge Cases and Failure Behavior

- Unauthenticated request to a tenant-scoped page → challenge (302 to login), not a crash.
- Subdomain present but the header also present → header wins.
- Slug with wrong case → normalise to lower-case before lookup.
- `Tenant.Status != Active` → treat as 403.
- Circuit opened with no resolvable tenant → `TenantState` stays unresolved, tenant pages render an explicit "select a tenant" state.

## 9. Existing Behavior That Must Remain Unchanged

- Blazor template pages still render; static assets still served.
- Antiforgery still applied.

## 10. Dependencies and Assumptions

- REQ-001 contracts exist exactly as named; `Program.cs` already has the auth slot in place.
- Local Postgres is up (`podman-compose up -d`).
- Dev-time seeding is acceptable and documented as non-production behaviour.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. Auth = ASP.NET Core Identity (decision #1).

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
podman-compose up -d && podman-compose ps
dotnet ef database update --context ControlPlaneDbContext -p src/Acentra.Infrastructure -s src/Acentra.Web
dotnet ef migrations list --context ControlPlaneDbContext -p src/Acentra.Infrastructure -s src/Acentra.Web
```

## 13. Expected Final State

A running app with real login, a control-plane database holding tenants + users + memberships, and a middleware pipeline that resolves and authorizes a tenant — or fails closed — on every request. `TenantState` is the single seam the UI switcher mutates.

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-002` (created once, by the Overseer, for the whole parallel batch).
- Final commit: `feat(auth): identity, tenant registry, membership, tenant resolution`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2. Do not touch `TenantData/`, `Storage/`, `Inventory/`, or `compose.yaml`.
2. Do not edit `.csproj` — REQ-001 added every package.
3. Do not run `dotnet ef` migrations for any context other than `ControlPlaneDbContext`.
4. If the REQ-001 contract disagrees with this plan, stop and report — do not rename interfaces.
