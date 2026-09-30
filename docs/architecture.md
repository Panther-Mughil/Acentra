# Architecture — Multi-Tenant Inventory Platform

Companion document to [`problem-statement.md`](./problem-statement.md).

---

## 1. Goals and non-goals

**Goals**

- Enforce tenant isolation at the **architecture** and **data-access** layers, not just in UI code.
- ASP.NET Core + EF Core backend with tenant scoping baked into the `DbContext`.
- Per-tenant inventory management, and per-tenant file storage (S3-compatible, hosted locally).
- Frontend that can **switch tenant context** and shows only that tenant's inventory.

**Non-goals (for now)**

- Cross-tenant reporting/analytics.
- Per-tenant custom schema or per-tenant database (see §5.4 for the trade-off).
- Billing/identity federation.

---

## 2. System context

```
                    ┌──────────────────────────────┐
   Browser ────────▶│  Frontend (React SPA  OR     │
   X-Tenant: acme   │  Blazor Web App)             │
                    └───────────────┬──────────────┘
                                    │ HTTPS  (JWT + tenant hint)
                                    ▼
                    ┌──────────────────────────────┐
                    │ ASP.NET Core API             │
                    │  ├─ TenantResolutionMiddleware│──▶ resolves TenantId
                    │  ├─ Authentication / AuthZ    │
                    │  ├─ Inventory endpoints       │
                    │  └─ File endpoints            │
                    └───────┬─────────────┬────────┘
                            │             │
                            ▼             ▼
                  ┌──────────────┐  ┌────────────────┐
                  │ EF Core      │  │ File storage   │
                  │ AppDbContext │  │ (local FS,     │
                  │ (global      │  │  S3-ready)     │
                  │  filters)    │  └────────────────┘
                  └──────┬───────┘
                         ▼
                  ┌──────────────┐
                  │ PostgreSQL   │
                  └──────────────┘
```

Key point: **the frontend is a thin client.** It can *request* a tenant, but isolation is decided server-side. A malicious or buggy client cannot widen its own scope.

---

## 3. Layered architecture (ASP.NET Core)

```
src/
├─ Acentra.Api              ASP.NET Core host
│   ├─ Middleware/          TenantResolutionMiddleware, TenantContext
│   ├─ Controllers/         Inventory, Files, Tenants
│   └─ Program.cs           DI wiring, pipeline order
├─ Acentra.Domain           Entities: Tenant, Product, StockLevel, StockMovement
├─ Acentra.Infrastructure   AppDbContext, EF configurations, migrations, S3 storage
└─ Acentra.Web              React SPA  OR  Blazor components
tests/
├─ Acentra.UnitTests
└─ Acentra.IntegrationTests  ← isolation tests live here (see §5.5)
```

For an assignment-sized build, `Api` + `Infrastructure` + `Web` is enough. Keep `Domain` free of EF/ASP.NET references so the invariants stay testable.

### Pipeline order (matters)

```csharp
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();  // AFTER auth, BEFORE endpoints
app.UseAuthorization();
app.MapControllers();
```

Tenant resolution must run **after** authentication so it can validate the requested tenant against the caller's claims, and **before** any endpoint touches the database.

---

## 4. Tenant resolution

`ITenantResolver` with pluggable strategies, chosen by config:

| Strategy | Example | Notes |
|---|---|---|
| Header | `X-Tenant: acme` | Simplest to test; good default for an API |
| Subdomain | `acme.inventory.app` | Natural for browser apps; needs wildcard DNS/cert |
| JWT claim | `tenant_id: acme` | Strongest — user can't self-select a foreign tenant |

Resolution flow:

1. Read the hint (header/subdomain/claim).
2. Resolve it to an internal `Tenant` row (cache by slug, TTL a few minutes).
3. **Authorize**: does the authenticated principal have a membership in that tenant? If not → `403`.
4. Publish into a scoped `ITenantContext { Guid TenantId; string Slug; }`.

Rules that keep this safe:

- `ITenantContext` is **scoped**, never singleton, never static.
- No tenant resolved → fail closed (`400`/`403`), never "all tenants".
- The client-supplied tenant id is a **hint**, not authority. Claims win.

---

## 5. Data isolation (defense in depth)

### 5.1 Schema

Every tenant-owned table carries a non-null `TenantId` and is indexed `(TenantId, …)`. Composite unique keys are tenant-scoped, e.g. `UNIQUE (TenantId, Sku)` — two tenants may both own `SKU-001`.

### 5.2 Global query filters

```csharp
public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>().HasQueryFilter(p => p.TenantId == _tenant.TenantId);
        b.Entity<StockMovement>().HasQueryFilter(m => m.TenantId == _tenant.TenantId);
        // …every tenant-owned entity
    }
}
```

- Filters are applied to **every** query, including `Include`/navigation loads, which is why they beat hand-written `WHERE` clauses.
- One `DbContext` **per request** (scoped) is mandatory — a shared/long-lived context caches one tenant's entities and leaks them to the next request.

### 5.3 Write-side enforcement

Query filters only cover reads. Add an `SaveChanges` interceptor that:

- Stamps `TenantId` on inserted tenant-owned entities from `ITenantContext`.
- Throws if an entity's `TenantId` differs from the current tenant (blocks smuggling another tenant's id in a request body).

### 5.4 Database-per-tenant vs. shared schema

| | Shared schema + `TenantId` | Database per tenant |
|---|---|---|
| Isolation strength | Logical (depends on filters) | Physical |
| Ops cost | Low | Migrations × N tenants, connection routing |
| Cost at low tenant count | Low | Higher |
| Right for | Most SaaS, this assignment | Regulated/noisy-neighbour workloads |

Start shared-schema; the EF filter + interceptor design is the same shape either way, and `ITenantContext` gives you the seam to move later.

### 5.5 Proving isolation

Isolation is a testable property, so test it:

1. Seed tenants A and B with identically named SKUs.
2. Query as A → only A's rows returned.
3. `PUT` a product with B's id while acting as A → `404`/`403`, never a cross-tenant write.
4. Request tenant B with A's token → `403`.
5. Omit the tenant hint → `400`/`403`, and zero rows.

### 5.6 Known leak vectors to close

- `IgnoreQueryFilters()` — audit every call site; restrict to admin paths.
- Raw SQL / `FromSqlRaw` — not filtered automatically.
- Background jobs / `IHostedService` — there is no HTTP request, so `ITenantContext` is empty. Pass an explicit tenant scope into the job.
- Migrations and seeders — bypass filters by design; never seed with request-scoped logic.
- Logs and error payloads — log the tenant slug, not cross-tenant row contents.

---

## 6. File storage

**Decision: local filesystem behind `IFileStorage`.** There is no AWS account for this project, so the S3 requirement is satisfied by an abstraction whose S3 implementation can be dropped in later. Pointing the same interface at **MinIO** (S3-compatible, runs locally in Docker) keeps the requirement literally true at zero cost — see §10.

```csharp
public interface IFileStorage
{
    Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct);
    Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, string key, CancellationToken ct);
    Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct);
}
```

- Layout: `<root>/tenants/{tenantId}/{category}/{guid}{ext}` — tenant prefix first, so the identical key shape works for a filesystem root or an S3 prefix.
- Keep the root **outside the repo** (e.g. `~/acentra-storage`) or in a gitignored `storage/` directory.
- Store only the **key** in the database, alongside the tenant id; re-verify the tenant on every read.
- Never trust a client-supplied key: resolve the object through the DB row, which is already query-filtered.
- The local impl returns a tokenised app URL instead of a presigned S3 URL — keep the signature identical so the swap is invisible to callers.
- Sanitise filenames; never let a client path component escape the tenant directory (`..`, absolute paths, symlinks).

---

## 7. Tenant switching in the frontend

- The selected tenant is app state (context/store) that drives the `X-Tenant` header (or the subdomain).
- Persist the choice (localStorage / route param) and re-fetch on change.
- On `401/403`, clear the selected tenant and force re-selection — don't silently fall back to another tenant.
- The tenant switcher should list **only** tenants the user is a member of.

---

## 8. React or Blazor — which is better?

Short answer: **neither is universally better, and for this problem it barely affects isolation**, because tenant enforcement lives in middleware + EF Core. The decision is about team, tooling, and UI ambition.

### Comparison

| Dimension | **Blazor** (Web App, .NET 10) | **React** (19.x) |
|---|---|---|
| Language | C# for UI and server — one language end to end | TypeScript + C#, two languages |
| Sharing code | Share `Domain` DTOs and validation directly, no DTO drift | Needs typed clients (OpenAPI/NSwag) to stay in sync |
| Project shape | Single ASP.NET Core app, or WASM SPA | Separate API + SPA; two deployables |
| API surface | Can call services directly; still expose REST for mobile/3rd-party | REST/GraphQL API is the product surface |
| Rendering | Static SSR, interactive Server (SignalR), WASM, per-component render modes | CSR by default; RSC/Next.js for SSR |
| Ecosystem | Growing, .NET-centric; fewer ready-made UI kits | Enormous: component libraries, charting, drag-drop, tables |
| Data grids / complex UX | Decent (QuickGrid, third-party), less mature | Best-in-class choices; usually the deciding factor for inventory UIs |
| Hiring | Easy if the team is already .NET | Easy for frontend specialists |
| Debugging / tooling | Unified .NET debugging; WASM debugging is heavier | Excellent browser devtools |
| Latency model | Server interactive needs a live connection (SignalR) | Plain HTTP; simpler to scale statically |
| Learning curve | Low for a C# team, unfamiliar for a JS team | Low for a JS team |

### Recommendation

- **Choose Blazor if:** the team is C#-first, this is one of several .NET services, and the UI is standard CRUD (tables, forms, filters). You get one language, one deployable, and no DTO sync work — the fastest path to a working demo. Use **interactive Server** render mode for the dashboard and static SSR for public pages.
- **Choose React if:** you want a polished, component-library-driven inventory UI (sortable/filterable grids, charts, drag-and-drop), a single API consumed by web + mobile, or you expect frontend specialists on the team.

> **Decision (locked): Blazor Web App, interactive Server render mode.** The app is C#-centric, the UI is CRUD-heavy, and it removes an entire API-contract layer.
>
> Revisit only if the UI becomes the differentiator and needs a mature third-party data-grid/charting ecosystem.

Whichever you pick, the multi-tenancy requirements (§4–§6) are unchanged — they are server-side concerns.

---

## 9. Stack summary

| Layer | Choice |
|---|---|
| Runtime | .NET (LTS) |
| API | ASP.NET Core Web API, controllers or minimal APIs |
| ORM | EF Core, one scoped `DbContext`, global query filters + `SaveChanges` interceptor |
| Tenant resolution | Middleware + `ITenantResolver` (header/subdomain/JWT claim) |
| Database | **PostgreSQL** (decided), via Npgsql |
| Files | **Local filesystem** behind `IFileStorage` (decided), S3/MinIO drop-in — `tenants/{tenantId}/…` prefix |
| Auth | JWT bearer (or ASP.NET Core Identity) with tenant membership claims |
| Frontend | **Blazor Web App, interactive Server render mode** (decided) + static SSR for public pages |
| Tests | xUnit unit tests + integration tests for the isolation matrix (§5.5) |

---

## 10. Open decisions

1. Tenant identification: header vs subdomain vs claim — **defaulting to header `X-Tenant`**, with the JWT claim authoritative when present.
2. Auth provider: local ASP.NET Core Identity vs standalone JWT bearer.
3. Whether database-per-tenant is ever needed (start shared-schema, §5.4).
4. Whether to run MinIO locally so file storage stays literally S3-compatible.

**Decided:** frontend = Blazor Web App, interactive Server (§8) · database = PostgreSQL · file storage = local filesystem behind `IFileStorage` (§6).
