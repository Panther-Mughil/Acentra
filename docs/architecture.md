# Architecture — Multi-Tenant Inventory Platform

Companion document to [`problem-statement.md`](./problem-statement.md).

---

## 0. Status

| Area | State |
| --- | --- |
| Solution scaffold (5 projects, `Acentra.slnx`) | ✅ committed — `608abb6` |
| Build / tests green | ✅ `dotnet build` 0 warnings · `dotnet test` 2/2 |
| Local Postgres via rootless podman | ✅ `postgres:17-alpine` verified accepting connections |
| Domain entities (`Tenant`, `Product`, `StockMovement`) | ⏳ next |
| EF Core + Npgsql, global query filters, write interceptor | ⏳ next |
| `ITenantContext` + `TenantResolutionMiddleware` | ⏳ next |
| Inventory UI (Blazor) + tenant switcher | ⏳ |
| `IFileStorage` local implementation | ⏳ |
| Authentication / authorization | ⏳ not started — open decision, §11 |

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

```text
                    ┌───────────────────────────────────┐
   Browser ────────▶│  Blazor Web App (interactive       │
   X-Tenant: acme   │  Server) — the only host           │
                    │  ├─ TenantResolutionMiddleware     │
                    │  ├─ Components (inventory, switch) │
                    │  └─ REST endpoints (same pipeline) │
                    └───┬───────────────┬───────────────┘
                        │               │
                        ▼               ▼
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

## 3. Solution layout (as scaffolded)

```text
Acentra.slnx                     .NET 10 XML solution format
compose.yaml                     rootless podman Postgres for local dev
docs/
src/
├─ Acentra.Domain/               entities — no EF Core / ASP.NET references
├─ Acentra.Infrastructure/       AppDbContext, EF configurations, migrations,
│                                IFileStorage implementations, tenant resolvers
└─ Acentra.Web/                  the single host — Blazor Web App
    ├─ Program.cs                DI wiring, middleware pipeline
    ├─ Components/
    │   ├─ App.razor, Routes.razor, _Imports.razor
    │   ├─ Layout/               MainLayout, NavMenu, ReconnectModal
    │   └─ Pages/                Home, Error, NotFound (+ inventory pages, TBD)
    └─ Middleware/               TenantResolutionMiddleware (TBD)
tests/
├─ Acentra.UnitTests/            pure domain / service logic
└─ Acentra.IntegrationTests/     WebApplicationFactory — isolation matrix (§5.5)
```

Reference graph: `Infrastructure → Domain`, `Web → Domain + Infrastructure`, `UnitTests → Domain + Infrastructure`, `IntegrationTests → Web`.

### Why there is no separate `Acentra.Api`

Earlier drafts split an API host from the web host. That was dropped:

- Blazor Web App **is** an ASP.NET Core host — it already runs the same middleware pipeline, DI container, and endpoint routing.
- Interactive Server components call services **in-process**, so a second deployable would add an HTTP hop, a serialization boundary, and token forwarding for zero isolation benefit.
- REST endpoints can live in the same host (controllers or minimal APIs) whenever a mobile or third-party client needs them, without moving anything.

Split it later only if the API needs an independent scale/deploy cadence.

### Render mode: all-interactive

The template was generated with `--interactivity Server --all-interactive`:

- The tenant switcher lives in the **layout**. A statically rendered parent cannot host interactive child components, so a per-page render-mode setup would break the switcher — the classic Blazor render-mode trap.
- Public pages can be downgraded to static SSR later by marking components as non-interactive.

### Pipeline order

```csharp
app.UseHttpsRedirection();
app.UseAuthentication();                          // (once auth lands, §11)
app.UseMiddleware<TenantResolutionMiddleware>();  // AFTER auth, BEFORE endpoints
app.UseAuthorization();
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();
```

Tenant resolution must run **after** authentication so it can validate the requested tenant against the caller's claims, and **before** any endpoint touches the database.

---

## 4. Tenant resolution

**Decision: header `X-Tenant` is the default hint**, with a JWT claim authoritative once auth exists. `ITenantResolver` keeps the strategies pluggable:

| Strategy | Example | Notes |
| --- | --- | --- |
| Header | `X-Tenant: acme` | **Chosen default.** Simplest to test from the API and the UI |
| Subdomain | `acme.inventory.app` | Natural for browser apps; needs wildcard DNS/cert |
| JWT claim | `tenant_id: acme` | Strongest — user can't self-select a foreign tenant. Becomes authoritative when auth lands |

Resolution flow:

1. Read the hint (header/subdomain/claim).
2. Resolve it to an internal `Tenant` row (cache by slug, TTL a few minutes).
3. **Authorize**: does the authenticated principal have a membership in that tenant? If not → `403`.
4. Publish into a scoped `ITenantContext { Guid TenantId; string Slug; }`.

Rules that keep this safe:

- `ITenantContext` is **scoped**, never singleton, never static.
- No tenant resolved → fail closed (`400`/`403`), never "all tenants".
- The client-supplied tenant id is a **hint**, not authority. Claims win.

### 4.1 Blazor Server circuits — the non-obvious part

With interactive Server, HTTP middleware runs **once per circuit** (at the initial request / SignalR negotiate), not once per UI interaction. The circuit then has its own DI scope, and `IHttpContextAccessor` is not reliable mid-circuit.

Consequences the implementation must respect:

- Capture the resolved tenant into a **circuit-scoped** `TenantState` at circuit start and pin it there.
- The UI **tenant switcher mutates `TenantState` explicitly** — this is what satisfies the "switch tenant context" requirement, since no header is re-sent over SignalR.
- `ITenantContext` reads from `TenantState`, never from `HttpContext`.
- Because the tenant can change mid-circuit, do not cache a `DbContext` across interactions — use `IDbContextFactory<AppDbContext>` and create a short-lived context per unit of work.
- Changing tenant must drop any cached query results in components.

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
- One `DbContext` **per unit of work** — a shared/long-lived context caches one tenant's entities and leaks them to the next request or tenant (§4.1).
- Filter expressions are captured **per model**, and EF caches the model. A filter that closes over a mutable service needs either a model cache key per tenant or a filter that reads the tenant at query time; verify this with the §5.5 tests rather than assuming.

### 5.3 Write-side enforcement

Query filters only cover reads. A `SaveChanges` interceptor will:

- Stamp `TenantId` on inserted tenant-owned entities from `ITenantContext`.
- Throw if an entity's `TenantId` differs from the current tenant (blocks smuggling another tenant's id in a request body).

### 5.4 Database-per-tenant vs. shared schema

| | Shared schema + `TenantId` | Database per tenant |
| --- | --- | --- |
| Isolation strength | Logical (depends on filters) | Physical |
| Ops cost | Low | Migrations × N tenants, connection routing |
| Cost at low tenant count | Low | Higher |
| Right for | Most SaaS, this assignment | Regulated/noisy-neighbour workloads |

**Decision: shared schema.** The EF filter + interceptor design is the same shape either way, and `ITenantContext` is the seam that makes a later move possible.

### 5.5 Proving isolation

Isolation is a testable property, so it is tested explicitly. The matrix lives in `Acentra.IntegrationTests` and drives the real pipeline via `WebApplicationFactory`:

1. Seed tenants A and B with identically named SKUs.
2. Query as A → only A's rows returned.
3. `PUT` a product with B's id while acting as A → `404`/`403`, never a cross-tenant write.
4. Request tenant B with A's credentials/claims → `403`.
5. Omit the tenant hint → `400`/`403`, and zero rows.
6. Switch tenant mid-session → previously loaded rows disappear (Blazor §4.1).

### 5.6 Known leak vectors to close

- `IgnoreQueryFilters()` — audit every call site; restrict to admin paths.
- Raw SQL / `FromSqlRaw` — not filtered automatically.
- Background jobs / `IHostedService` — there is no HTTP request, so `ITenantContext` is empty. Pass an explicit tenant scope into the job.
- Migrations and seeders — bypass filters by design; never seed with request-scoped logic.
- Logs and error payloads — log the tenant slug, not cross-tenant row contents.
- Blazor circuits outliving a "request" (§4.1) — the most likely leak in this design; the switcher must invalidate cached component state.

---

## 6. File storage

**Decision: local filesystem behind `IFileStorage`.** There is no AWS account for this project, so the S3 requirement is met by an abstraction whose S3 implementation can be dropped in later. Pointing it at **MinIO** — S3-compatible, runs locally under the same podman setup — keeps the requirement literally true at zero cost; that remains an open question in §11.

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
- Root location is an open decision (§11); it must be **outside version control** either way.
- Store only the **key** in the database, alongside the tenant id; re-verify the tenant on every read.
- Never trust a client-supplied key: resolve the object through the DB row, which is already query-filtered.
- The local impl returns a tokenised app URL instead of a presigned S3 URL — keep the signature identical so the swap is invisible to callers.
- Sanitise filenames; never let a client path component escape the tenant directory (`..`, absolute paths, symlinks).

---

## 7. Tenant switching in the frontend

- Selected tenant lives in the circuit-scoped `TenantState` (§4.1), exposed to components via a cascading value.
- Selection persists across reloads (localStorage) and drives the `X-Tenant` header on any REST call and the initial SignalR negotiate.
- On `401/403`, clear the selection and force re-selection — never silently fall back to another tenant.
- The switcher lists **only** tenants the user is a member of.
- Switching must clear cached page data so the previous tenant's inventory cannot linger on screen.

---

## 8. React or Blazor — which is better?

Short answer: **neither is universally better, and for this problem it barely affects isolation**, because tenant enforcement lives in middleware + EF Core. The decision is about team, tooling, and UI ambition.

### Comparison

| Dimension | **Blazor** (Web App, .NET 10) | **React** (19.x) |
| --- | --- | --- |
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

- **Choose Blazor if:** the team is C#-first, this is one of several .NET services, and the UI is standard CRUD (tables, forms, filters). One language, one deployable, no DTO sync — the fastest path to a working demo.
- **Choose React if:** you want a polished, component-library-driven inventory UI (sortable/filterable grids, charts, drag-and-drop), a single API consumed by web + mobile, or you expect frontend specialists on the team.

> **Decision (locked): Blazor Web App, interactive Server render mode, all-interactive.**
> The app is C#-centric, the UI is CRUD-heavy, and it removes an entire API-contract layer.
> Revisit only if the UI becomes the differentiator and needs a mature third-party data-grid/charting ecosystem.

Either way, the multi-tenancy requirements (§4–§6) are unchanged — they are server-side concerns.

---

## 9. Stack (as built)

| Layer | Choice |
| --- | --- |
| Runtime | **.NET 10** (`net10.0`), SDK `10.0.112` |
| Solution format | **`Acentra.slnx`** — the .NET 10 XML solution |
| Host | **Blazor Web App**, interactive Server, all-interactive (`Acentra.Web`) |
| Data access | EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` (to be added) |
| Database | **PostgreSQL 17.11** — `postgres:17-alpine`, rootless podman (§10) |
| Tenant resolution | Middleware + `ITenantResolver`; header `X-Tenant` default |
| Isolation | Tenant-scoped schema + EF Core global query filters + `SaveChanges` interceptor |
| Files | **Local filesystem** behind `IFileStorage`; S3/MinIO drop-in |
| Auth | Not yet implemented — ASP.NET Core Identity vs JWT bearer (§11) |
| Tests | xUnit — `Acentra.UnitTests` + `Acentra.IntegrationTests` (isolation matrix, §5.5) |

---

## 10. Local development environment

| Item | Value |
| --- | --- |
| Postgres image | `docker.io/library/postgres:17-alpine` (verified 17.11) |
| Container | `acentra-postgres`, volume `acentra-pgdata` |
| Host port | `5432` (override with `POSTGRES_PORT`) |
| Database / user / password | `acentra` / `acentra` / `acentra_dev_password` |
| Connection string | `Host=localhost;Port=5432;Database=acentra;Username=acentra;Password=acentra_dev_password` |
| Commands | `podman-compose up -d` · `podman-compose ps` · `podman-compose down [-v]` |

Notes that cost time if forgotten:

- Publish ports explicitly (`"5432:5432"`); `expose`-only would make the database unreachable from `localhost`.
- `podman-compose down -v` **wipes** the volume.
- Images are fully qualified (`docker.io/...`) because podman does not resolve short names from Docker Hub by default.
- The .NET SDK on Arch/CachyOS ships **without** the ASP.NET Core runtime/targeting pack; without `aspnet-runtime` and `aspnet-targeting-pack` every web project fails with `NETSDK1226: Prune Package data not found .NETCoreApp 10.0 Microsoft.AspNetCore.App`.

---

## 11. Decisions and open questions

**Locked**

| Decision | Where |
| --- | --- |
| Blazor Web App, interactive Server, single host (no separate API project) | §3, §8 |
| PostgreSQL (shared schema, `TenantId` column + global query filters) | §5.4, §9 |
| Local file storage behind `IFileStorage` (no AWS account) | §6 |
| Header `X-Tenant` as the default tenant hint | §4 |
| `Acentra.slnx` as the solution format | §3 |

**Open**

1. Auth provider: local ASP.NET Core Identity vs standalone JWT bearer — blocks making the tenant claim authoritative (§4).
2. Whether to run MinIO alongside Postgres so file storage is literally S3-compatible (§6).
3. File storage root path for local dev (`~/acentra-storage` vs a gitignored `storage/`).
4. Whether database-per-tenant is ever needed (§5.4).
5. Whether the inventory UI needs anything beyond Blazor's built-in components — the one criterion that would reopen §8.
