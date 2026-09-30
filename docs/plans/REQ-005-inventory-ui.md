# REQ-005 — Inventory services + simple placeholder Blazor UI + tenant switcher

**Risk Level:** High — requirement #8 (switch tenant context) and #9 (show only the selected tenant's inventory) live here, plus the Blazor-circuit leak vector flagged in architecture §4.1/§5.6.

> **Execution order:** runs **after** REQ-002/003/004 have landed and been integrated (it consumes all three). Sequential.

## 1. Exact Feature Request and Clarified Requirements

The frontend is being built by another team and will arrive later, so this plan deliberately builds a **simple, disposable UI** over a **stable service layer**. When the team's frontend lands, only the `.razor` files are replaced — `IInventoryService` and `IFileStorage` do not change.

Deliver:
- `IInventoryService` — tenant-scoped product/stock operations built on `ITenantDbContextFactory`.
- Minimal Blazor pages: product list, create/edit, delete; stock adjust; a simple file upload per product.
- A **tenant switcher** in the layout that lists only the tenants the signed-in user is a member of, mutates circuit-scoped `TenantState`, and **clears cached page data** so the previous tenant's rows cannot linger on screen.
- An explicit "select a tenant" empty state when nothing is resolved.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Infrastructure/Inventory/**`, `src/Acentra.Web/Components/Pages/Inventory/**`, `src/Acentra.Web/Components/Layout/**`, `src/Acentra.Web/Components/_Imports.razor`, `src/Acentra.Web/Components/Routes.razor`.
- **In-Scope:** inventory service, validation, minimal pages, switcher, tenant-change invalidation.
- **Out of Scope:** the team's real frontend, styling/design system, charts, drag-and-drop, barcode scanning, multi-warehouse, auth pages (REQ-002), provisioning (REQ-003), storage internals (REQ-004), the isolation test matrix (REQ-006).

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Infrastructure/Inventory/IInventoryService.cs` | Add | frozen service contract |
| `src/Acentra.Infrastructure/Inventory/InventoryService.cs` | Add | EF-backed implementation |
| `src/Acentra.Infrastructure/Inventory/ServiceCollectionExtensions.cs` | Complete stub | `AddInventory` |
| `src/Acentra.Web/Components/Layout/TenantSwitcher.razor` | Add | requirement #8 |
| `src/Acentra.Web/Components/Layout/MainLayout.razor` | Modify | host the switcher |
| `src/Acentra.Web/Components/Pages/Inventory/Products.razor` | Add | list + delete (requirement #9) |
| `src/Acentra.Web/Components/Pages/Inventory/ProductEdit.razor` | Add | create/edit + upload |
| `src/Acentra.Web/Components/Pages/Inventory/StockAdjust.razor` | Add | stock movements |
| `src/Acentra.Web/Components/Routes.razor` | Modify | authorize inventory routes |

## 3. Current Architecture / Context

- `ITenantContext` is satisfied by the **circuit-scoped, mutable** `TenantState` (REQ-002) with a `Changed` event. `ITenantContext` must never be read from `HttpContext` mid-circuit.
- `ITenantDbContextFactory.Create()` (REQ-003) throws when the tenant is unresolved and returns a **short-lived** `AppDbContext` for the current tenant. A context must never be held in a component field across interactions.
- `IFileStorage` (REQ-004) stores objects under `tenants/{tenantId}/...`; `TenantFile` metadata rows live in the tenant database.
- The app is **all-interactive** Server render mode, so the switcher can live in the layout (architecture §3).

## 4. Implementation Requirements & Interfaces

```csharp
public interface IInventoryService {
    Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken ct = default);
    Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default);
    Task<Product> CreateProductAsync(ProductInput input, CancellationToken ct = default);
    Task<Product> UpdateProductAsync(Guid id, ProductInput input, CancellationToken ct = default);
    Task DeleteProductAsync(Guid id, CancellationToken ct = default);
    Task<StockLevel> AdjustStockAsync(Guid productId, int delta, string reason, string? note, CancellationToken ct = default);
    Task<IReadOnlyList<StockMovement>> ListMovementsAsync(Guid productId, CancellationToken ct = default);
}
public sealed record ProductInput(string Sku, string Name, string? Description, decimal UnitPrice, int ReorderLevel, bool IsActive);
```

Rules:
- Every method opens a context through `ITenantDbContextFactory.Create()` inside a `using` and never stores it.
- `DeleteProductAsync` and `AdjustStockAsync` must not be able to touch another tenant's row: the query is filter-protected, so "not found" is the correct cross-tenant answer (`KeyNotFoundException`), never a cross-tenant write.
- Validation: `Sku` required, `^[A-Z0-9-]{2,32}$`, unique per tenant (rely on the tenant-scoped unique index **and** a friendly pre-check); `UnitPrice >= 0`; `ReorderLevel >= 0`; `delta != 0`.

**Tenant switcher:**
- Lists `ITenantRegistry.FindForUserAsync(currentUserId)` — **only** tenants the user is a member of.
- Calls `TenantState.Set(...)` on selection; persists the choice in `localStorage` and re-applies it on circuit start.
- On `401/403` clears the selection and forces re-selection — **never** silently falls back to another tenant.

**Cache invalidation (the leak vector):**
- Each inventory page subscribes to `TenantState.Changed` and, on fire, **clears its loaded rows and re-queries**.
- The list page must bind to a field that is explicitly reset; it must not rely on `OnInitializedAsync` running again (a circuit does not re-run it on tenant change).
- A defensive re-check compares the `TenantId` of loaded rows against `TenantState.TenantId` before rendering and drops any mismatch.

**Routing:** inventory routes require authorization; with no tenant resolved they render the "select a tenant" state rather than an error.

## 5. Step-by-Step Implementation Plan

1. `IInventoryService` + `ProductInput`; `InventoryService` on `ITenantDbContextFactory`; complete `AddInventory` (scoped).
2. `TenantSwitcher.razor` + wire into `MainLayout`/`NavMenu`; tenant list from `ITenantRegistry`; `localStorage` persistence.
3. `Products.razor` — list for the current tenant, delete with confirmation, subscribes to `Changed`.
4. `ProductEdit.razor` — create/edit, validation, optional file upload through `IFileStorage` writing a `TenantFile` row.
5. `StockAdjust.razor` — adjust quantity, write a `StockMovement`, list movements.
6. `Routes.razor` — authorise inventory routes; add the unresolved-tenant empty state.
7. `dotnet build` + `dotnet test`; manual smoke test with the two seeded tenants.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; `dotnet test` → all green.
- [ ] Sign in as the seeded multi-tenant user; the switcher lists exactly the tenants they belong to.
- [ ] Switch `acme → globex` on the products page → rows are replaced by globex's rows; **no acme row remains visible** (requirement #9).
- [ ] Both tenants may own `SKU-001`; both are visible under their own tenant.
- [ ] Deleting/reading a product id belonging to the other tenant → treated as not found; no cross-tenant mutation.
- [ ] Adjusting stock writes a `StockMovement` and updates `StockLevel` atomically.
- [ ] Reloading the page preserves the selected tenant (requirement #8 across reloads).
- [ ] Unresolved tenant → "select a tenant" state, no data and no exception.
- [ ] Uploading a file stores an object under `tenants/{tenantId}/...` and a `TenantFile` row in that tenant's database.

**Required Tests / Demonstrated Behavior:** service-level unit tests (validation, tenant-scoped not-found behaviour) — the cross-tenant UI assertions are formalised in REQ-006.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- `IInventoryService` is the frontend team's contract — keep it stable and UI-agnostic; no `HttpContext`, no `ComponentBase` types leaking into it.
- No `DbContext` stored in a component or service field across interactions.
- Switching tenants must never leave the previous tenant's data rendered.
- Isolation is enforced by REQ-002/003 — **no tenant filtering logic in the UI**.

**Non-Goals:** design/styling, responsive polish, dark mode, pagination/sorting beyond the basics, real-time updates, multi-user concurrency UI, replacing the layout.

## 8. Edge Cases and Failure Behavior

- Tenant switched while an async load is in flight → discard the stale result (compare the tenant id captured at load time against the current one).
- Duplicate `Sku` → friendly validation message, not an EF exception surfaced to the user.
- Deleting a product that has stock movements → block with a clear message (do not silently cascade).
- Upload with a disallowed extension/size → rejected before hitting storage.
- Circuit reconnect → `TenantState` rehydrates from `localStorage`, and the loaded rows match it.

## 9. Existing Behavior That Must Remain Unchanged

- Home/Counter/Weather/Error/NotFound pages still render.
- Layout/nav structure keeps working; the switcher is additive.
- REQ-002/003/004 interfaces and configuration keys unchanged.

## 10. Dependencies and Assumptions

- REQ-002 (auth + `TenantState` + registry), REQ-003 (`ITenantDbContextFactory` + filters), REQ-004 (`IFileStorage`) have landed and are integrated.
- Two seeded tenants and one multi-tenant demo user exist (REQ-002).
- The frontend team replaces the `.razor` files later; the service layer is what must survive.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. Blazor confirmed (decision #5); the placeholder UI is explicitly disposable.

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet run --project src/Acentra.Web
```

## 13. Expected Final State

A working end-to-end demo: sign in, pick a tenant, see only that tenant's inventory, create/edit/delete products, adjust stock, attach a file, and switch tenants with the screen fully repopulating. The `.razor` layer is thin and replaceable; `IInventoryService` and `IFileStorage` are the frozen seams.

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-005`.
- Final commit: `feat(inventory): service layer, placeholder Blazor UI, tenant switcher`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2. Do not modify EF configuration, migrations, storage internals, or Identity.
2. Keep the UI deliberately simple — no styling rabbit holes, no component library.
3. Do not add tenant filtering to components; fix it in REQ-002/003 territory and report instead.
4. Prove the tenant-switch criterion manually (or via a test) and report the evidence — this is the requirement most likely to be declared done without being true.
