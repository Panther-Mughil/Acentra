# REQ-003 — Tenant data plane: database-per-tenant, global query filters, write interceptor, provisioning

**Risk Level:** High — this is requirement #3 (query filters), #6 (per-tenant inventory storage) and #11 (isolation) all at once, and it is the file most likely to leak data if wrong.

> **Execution order:** runs **in parallel** with REQ-002 and REQ-004, after REQ-001. Owns `src/Acentra.Infrastructure/TenantData/**` and `src/Acentra.Infrastructure/Migrations/Tenant/**` only.

## 1. Exact Feature Request and Clarified Requirements

The decision changed from the current `architecture.md` §5.4: **database-per-tenant** (decision #4), not shared schema.

Because the brief *also* demands EF Core **global query filters** (requirement #3), the design keeps **both** layers:

- **Physical isolation** — one PostgreSQL database per tenant, connection resolved from `ITenantContext`. A wrong-tenant read is impossible without a routing bug.
- **Logical isolation** — `TenantId` stays on every tenant-owned entity and every query still gets `HasQueryFilter(e => e.TenantId == _tenant.TenantId)`. A routing bug alone still cannot leak across tenants; a filter bug alone cannot either.
- **Write-side enforcement** — a `SaveChanges` interceptor stamps `TenantId` on insert and throws when a row's `TenantId` does not match the current tenant (blocks smuggling another tenant's id through a request body).

Provisioning creates a new tenant's database and applies tenant migrations.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Infrastructure/TenantData/**`, `src/Acentra.Infrastructure/Migrations/Tenant/**`, `src/Acentra.Web/appsettings*.json`.
- **In-Scope:** `AppDbContext`, `ITenantDbContextFactory`, `TenantStampInterceptor`, entity configurations, tenant migrations + design-time factory, `TenantProvisioner`, `TenantDatabaseInitializer`.
- **Out of Scope:** control-plane context/Identity (REQ-002), storage (REQ-004), inventory service + UI (REQ-005), isolation tests (REQ-006).

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Infrastructure/TenantData/AppDbContext.cs` | Add | per-tenant context + global filters |
| `src/Acentra.Infrastructure/TenantData/Configurations/*.cs` | Add | indexes, tenant-scoped unique keys |
| `src/Acentra.Infrastructure/TenantData/TenantStampInterceptor.cs` | Add | write-side enforcement |
| `src/Acentra.Infrastructure/TenantData/TenantDbContextFactory.cs` | Add | connection routing |
| `src/Acentra.Infrastructure/TenantData/TenantProvisioner.cs` | Add | `CREATE DATABASE` + migrate |
| `src/Acentra.Infrastructure/TenantData/AppDbContextFactory.cs` | Add | design-time factory for `dotnet ef` |
| `src/Acentra.Infrastructure/TenantData/ServiceCollectionExtensions.cs` | Complete stub | `AddTenantData` |
| `src/Acentra.Infrastructure/Migrations/Tenant/**` | Add | `InitialTenant` migration |

## 3. Current Architecture / Context

- REQ-001 defines `Product`, `StockLevel`, `StockMovement`, `TenantFile` (each with `Guid TenantId`), `ITenantContext`, `TenantDescriptor`, and the stub `AddTenantData`.
- REQ-002 resolves the tenant; `ITenantContext` is satisfied by `TenantState` and is **circuit-scoped and mutable** — so a `DbContext` must never be cached across interactions.
- `TenantDescriptor.DatabaseName` is the per-tenant database name produced by REQ-002's registry.

## 4. Implementation Requirements & Interfaces

```csharp
public sealed class AppDbContext : DbContext {
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<TenantFile> Files => Set<TenantFile>();

    protected override void OnModelCreating(ModelBuilder b) {
        // Applied to EVERY tenant-owned entity, including navigation/Include loads.
        b.Entity<Product>().HasQueryFilter(p => p.TenantId == _tenant.TenantId);
        b.Entity<StockLevel>().HasQueryFilter(s => s.TenantId == _tenant.TenantId);
        b.Entity<StockMovement>().HasQueryFilter(m => m.TenantId == _tenant.TenantId);
        b.Entity<TenantFile>().HasQueryFilter(f => f.TenantId == _tenant.TenantId);
    }
}
```

**EF model-cache trap (must be handled, not assumed away).** EF caches the compiled model, and a filter closing over a capture object is compiled once. This context therefore takes the tenant as a **constructor parameter stored in a field**, and overrides `OnModelCreating` using that field, and the factory creates a **new context per unit of work** — so the field is correct at model-build time for that instance. Because EF caches the *model* (not the field), the filter must be written against the **context instance's** member. Validate this empirically in REQ-006; do not rely on a comment.

```csharp
public interface ITenantDbContextFactory {
    AppDbContext Create();                                   // current tenant (throws if unresolved)
    AppDbContext CreateForTenant(TenantDescriptor tenant);    // explicit tenant (jobs/provisioning)
}
```

`Create()` builds options with `UseNpgsql(<connection for current tenant>)` and adds `new TenantStampInterceptor(_tenant)`; it throws `InvalidOperationException` when `ITenantContext.IsResolved == false` — fail closed. Options are built **per call**, never cached, because the tenant can change mid-circuit.

**`TenantStampInterceptor : SaveChangesInterceptor`:**
- On `SavingChanges`/`SavingChangesAsync`, for every `Added` tenant-owned entry: set `TenantId` from `ITenantContext` when empty; **throw** if it already carries a different `TenantId`.
- For every `Modified`/`Deleted` tenant-owned entry: **throw** if `TenantId != ITenantContext.TenantId`.

**Schema rules:** every tenant-owned table has `TenantId` (`uuid`, non-null) and an index `(TenantId, …)`; unique keys are tenant-scoped — `UNIQUE (TenantId, Sku)`, `UNIQUE (TenantId, ProductId)` on `StockLevel`. Any two tenants may both own `SKU-001`.

**Provisioning:**
```csharp
public interface ITenantProvisioner {
    Task ProvisionAsync(TenantDescriptor tenant, CancellationToken ct); // CREATE DATABASE + migrate
}
```
- `TenantDatabaseInitializer : IHostedService` applies tenant migrations to every registered tenant DB at startup (dev convenience; idempotent).
- Guarantee no pending model changes: `dotnet ef migrations has-pending-model-changes --context AppDbContext` must return clean.

## 5. Step-by-Step Implementation Plan

1. `AppDbContext` with the tenant field, the four query filters, `UseSnakeCaseNamingConvention`-free default mapping (keep EF names) and `MigrationsAssembly("Acentra.Infrastructure")`.
2. Entity configurations: `TenantId` required + indexed, tenant-scoped unique keys, `decimal(18,2)` for `UnitPrice`, `RowVersion`/`xmin` if trivial (optional — do not expand scope).
3. `TenantStampInterceptor` with the insert/modify/delete rules.
4. `TenantDbContextFactory` (per-call options, no caching) + DI registration.
5. `IDesignTimeDbContextFactory<AppDbContext>` for `dotnet ef` (reads `ConnectionStrings:TenantTemplate`).
6. Apply the `TenantTemplate` connection-string format `{0}` → database name, and a `TenantDatabaseName` helper that sanitises slugs.
7. `InitialTenant` migration: `dotnet ef migrations add InitialTenant --context AppDbContext -o Migrations/Tenant -p src/Acentra.Infrastructure -s src/Acentra.Web`.
8. `TenantProvisioner` + `TenantDatabaseInitializer`.
9. `dotnet build` + `dotnet test`.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; `dotnet test` → 2/2.
- [ ] `dotnet ef migrations list --context AppDbContext` shows `InitialTenant`; `dotnet ef migrations has-pending-model-changes --context AppDbContext` reports clean.
- [ ] `Create()` throws when `ITenantContext.IsResolved == false` (proves fail-closed).
- [ ] Provisioning tenant B creates a **separate database** and tenant A's database is untouched.
- [ ] Inserting a product without `TenantId` is stamped from `ITenantContext`.
- [ ] Inserting/modifying a product whose `TenantId` differs from the current tenant **throws**.
- [ ] A query run on tenant A's database returns only rows with A's `TenantId`, proving the filter coexists with the physical split.

**Required Tests / Demonstrated Behavior:** the four assertions above as automated tests (REQ-006 completes the full matrix).

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- `AppDbContext` is **never** registered as a long-lived/shared service; always created through `ITenantDbContextFactory`.
- No query filter may be removed, and `IgnoreQueryFilters()` must not be called anywhere in this plan.
- `TenantId` remains on every tenant-owned entity, despite physical separation.
- No raw SQL in the app path (`FromSqlRaw`/`ExecuteSqlRaw`) except `CREATE DATABASE`, which is inherently non-filtered and must live only in `TenantProvisioner`.

**Non-Goals:** control-plane schema, Identity, storage, UI, cross-tenant reporting, per-tenant connection pooling tuning, read replicas.

## 8. Edge Cases and Failure Behavior

- Tenant database missing at query time → throw a clear exception; never fall back to another tenant's DB.
- Tenant slug that yields an invalid DB name (uppercase, `-`, length) → sanitise deterministically.
- Provisioning a tenant twice → idempotent (check `pg_database` first).
- `CREATE DATABASE` fails because of concurrent creation → treat `42P04` (duplicate_database) as success.
- Filter + physical split disagree (simulated wrong connection) → the filter still returns zero rows; that is the required behaviour.

## 9. Existing Behavior That Must Remain Unchanged

- Control-plane migrations (REQ-002) untouched.
- `dotnet build`/`dotnet test` remain green.
- Existing Blazor pages still render.

## 10. Dependencies and Assumptions

- REQ-001 contracts verbatim; REQ-002 supplies `TenantDescriptor.DatabaseName`.
- Local Postgres reachable; the dev user may `CREATE DATABASE` (it is the database owner).
- Concurrent execution note: this plan and REQ-002 both add migrations to `Acentra.Infrastructure`, but in **disjoint output folders** with distinct snapshot class names (`AppDbContextModelSnapshot` vs `ControlPlaneDbContextModelSnapshot`).

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. Database-per-tenant chosen (decision #4); global query filters retained to satisfy requirement #3.

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet ef migrations list --context AppDbContext -p src/Acentra.Infrastructure -s src/Acentra.Web
dotnet ef migrations has-pending-model-changes --context AppDbContext -p src/Acentra.Infrastructure -s src/Acentra.Web
podman exec acentra-postgres psql -U acentra -d postgres -c '\l'
```

## 13. Expected Final State

A tenant's data lives in its own PostgreSQL database, accessed through a short-lived `AppDbContext` built per unit of work by `ITenantDbContextFactory`, with global query filters and a write interceptor layered on top. Two tenants with identical SKUs coexist without collision.

## 14. Version Control / Checkpoint Strategy

- Checkpoint once for the whole parallel batch: `checkpoint: pre-REQ-002..004`.
- Final commit: `feat(tenant-data): database-per-tenant with global query filters and write interceptor`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2. Do not touch `ControlPlane/`, `Storage/`, `Inventory/`, `compose.yaml`, or `.csproj`.
2. Only run `dotnet ef` against `AppDbContext`.
3. Do not weaken or remove a query filter to make a test pass — report the conflict instead.
4. If EF's model cache makes the filter behave per-instance incorrectly, stop and report with evidence rather than inventing a workaround.
