# REQ-001 — Foundation: domain entities, contracts, DI skeleton, tooling, local MinIO

**Risk Level:** High — every later plan compiles against the contracts fixed here. A wrong contract here breaks all parallel work.

> **Execution order:** this plan runs **first and alone**. REQ-002/003/004 run in parallel **only after** this plan lands green.

## 1. Exact Feature Request and Clarified Requirements

Settle the whole codebase's shape before feature work fans out:

- Domain entities for tenants, inventory and files.
- Every cross-module **interface/contract** that later plans implement, created as compiling stubs.
- All NuGet package references added **once**, so no parallel plan has to edit a `.csproj`.
- `Program.cs` middleware + DI pipeline that already calls the four module registration extension methods (which exist as stubs here).
- Local MinIO added to `compose.yaml`; gitignored `storage/` root added.
- `dotnet-ef` global tool installed (currently **absent** — verified).

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Domain/**`, `src/Acentra.Infrastructure/**`, `src/Acentra.Web/Program.cs`, `src/Acentra.Web/appsettings*.json`, `compose.yaml`, `.gitignore`, `*.csproj` under `src/` and `tests/`.
- **In-Scope:** entities, interfaces, stubs, packages, DI/pipeline wiring, compose + gitignore.
- **Out of Scope:** real EF configuration, migrations, middleware bodies, storage implementations, UI pages, tests. Those are REQ-002…REQ-006.
- **Out of Scope:** changing `Acentra.slnx` project list.

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Domain/Abstractions/*.cs` | Add | `ITenantContext`, `IFileStorage`, `TenantDescriptor`, enums |
| `src/Acentra.Domain/Entities/*.cs` | Add | `Tenant`, `TenantMembership`, `Product`, `StockLevel`, `StockMovement`, `TenantFile` |
| `src/Acentra.Domain/Class1.cs` | Delete | template leftover |
| `src/Acentra.Infrastructure/Class1.cs` | Delete | template leftover |
| `src/Acentra.Infrastructure/{ControlPlane,TenantData,Storage,Inventory}/**` | Add | modules + stubbed `Add*` extension methods |
| `src/Acentra.Web/Program.cs` | Modify | pipeline order + 4 `Add*` calls |
| `src/Acentra.Web/appsettings.json` | Modify | connection strings + `Storage` section |
| `compose.yaml` | Modify | add MinIO service |
| `.gitignore` | Modify | ignore `storage/` |
| `*.csproj` | Modify | package references |

## 3. Current Architecture / Context

- 5 projects; `Infrastructure → Domain`, `Web → Domain + Infrastructure`.
- `Program.cs` is the untouched Blazor template: `AddRazorComponents().AddInteractiveServerComponents()`, `UseHttpsRedirection`, `UseAntiforgery`, `MapRazorComponents<App>().AddInteractiveServerRenderMode()`. **No** auth, **no** tenant middleware.
- Toolchain verified on this host: .NET SDK `10.0.112`, `podman`, `podman-compose`. **`dotnet-ef` is NOT installed.**

## 4. Implementation Requirements & Interfaces

**Domain — no EF Core / ASP.NET references.**

```csharp
// Abstractions
public interface ITenantContext { Guid TenantId { get; } string Slug { get; } bool IsResolved { get; } }
public interface IFileStorage {
    Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct);
    Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct);
    Task DeleteAsync(Guid tenantId, string key, CancellationToken ct);
    Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct);
}
public sealed record TenantDescriptor(Guid Id, string Slug, string Name, string DatabaseName);
public enum MembershipRole { Owner, Admin, Member, Viewer }
```

**Entities** (tenant-owned entities each carry `Guid TenantId`, non-nullable):
`Tenant(Id, Slug, Name, DatabaseName, Status, CreatedUtc)` · `TenantMembership(Id, TenantId, UserId, Role)` · `Product(Id, TenantId, Sku, Name, Description, UnitPrice, ReorderLevel, IsActive, CreatedUtc, UpdatedUtc)` · `StockLevel(Id, TenantId, ProductId, Quantity, UpdatedUtc)` · `StockMovement(Id, TenantId, ProductId, Delta, Reason, Note, OccurredUtc)` · `TenantFile(Id, TenantId, Key, FileName, ContentType, SizeBytes, UploadedUtc)`.

`TenantId` **stays on every tenant-owned entity even though each tenant has its own database** — it is what the global query filters (REQ-003, requirement #3) and the write interceptor key off.

**Infrastructure module contracts (stubs only in this plan):**

| Contract | Folder | Implements |
| --- | --- | --- |
| `ControlPlaneDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>` + `ITenantRegistry` | `ControlPlane/` | REQ-002 |
| `AppDbContext` + `ITenantDbContextFactory` + `TenantStampInterceptor` | `TenantData/` | REQ-003 |
| `IStorageOptions` / `LocalFileStorage` / `S3FileStorage` | `Storage/` | REQ-004 |
| `IInventoryService` / `InventoryService` | `Inventory/` | REQ-005 |

Each module folder exposes exactly one DI extension method, so `Program.cs` never changes again:

```csharp
services.AddControlPlane(configuration);
services.AddTenantData(configuration);
services.AddFileStorage(configuration);
services.AddInventory();
```

**Packages** — add without pinning so NuGet resolves the latest 10.x-compatible build; confirm with `dotnet restore`:
`Acentra.Infrastructure`: `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `AWSSDK.S3`.
`Acentra.Web`: `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Design` (startup project for `dotnet ef`).
`tests/Acentra.IntegrationTests`: `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.EntityFrameworkCore` (already has xUnit).

**Tooling:** `dotnet tool install --global dotnet-ef` (install into `~/.dotnet/tools`; add to `PATH` for the session).

**`compose.yaml`:** add a `minio` service, API `9000`, console `9001`, named volume `acentra-minio-data`, healthcheck `curl -f http://localhost:9000/minio/health/live`, credentials from env with dev defaults.

> **AMENDMENT (verified by the Overseer during Phase-1 verification):** the image must be **`docker.io/pgsty/minio:latest`**, not `docker.io/minio/minio`. Upstream MinIO has withdrawn its Docker Hub images — **every** tag of `docker.io/minio/minio` now returns `requested access to the resource is denied`, and `quay.io/minio/minio` returns `unauthorized`. `pgsty/minio` is a community mirror of the same MinIO server code; the container was verified to serve `HTTP 200` on `/minio/health/live` (`RELEASE.2026-08-04`). `curl`, `mc` and `bash` are all present in that image, so the healthcheck command above is valid and was confirmed to exit 0.
>
> REQ-004 owns this line from here on — if the image is replaced again, REQ-004 updates it and this note.

**`appsettings.json`:** add `ConnectionStrings:ControlPlane`, `ConnectionStrings:TenantTemplate` (with `{0}` for the database name), and a `Storage` section (`Provider`, `LocalRoot`, `Endpoint`, `Bucket`, `AccessKey`, `SecretKey`, `UseSsl`, `Region`).

## 5. Step-by-Step Implementation Plan

1. Delete `Class1.cs` in Domain and Infrastructure.
2. Add `Abstractions/` and `Entities/` to `Acentra.Domain` exactly per §4.
3. Add the four Infrastructure module folders, each with its contract types (stubbed bodies throwing `NotImplementedException` **only** where behaviour is not yet defined; DI extension methods must be real and register what exists).
4. Add all package references; `dotnet restore`.
5. Install `dotnet-ef` global tool.
6. Rewrite `Program.cs` to the §4 pipeline order, calling the four `Add*` methods.
7. Extend `appsettings.json` + `appsettings.Development.json`.
8. Add the MinIO service to `compose.yaml`.
9. Add `storage/` to `.gitignore`.
10. `dotnet build Acentra.slnx` → 0 warnings; `dotnet test Acentra.slnx` → still 2/2.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` succeeds with **0 warnings, 0 errors**.
- [ ] `dotnet test Acentra.slnx` passes (2/2 existing tests still green).
- [ ] Every contract in §4 exists at the stated path with the stated signature.
- [ ] `Program.cs` calls `AddControlPlane`, `AddTenantData`, `AddFileStorage`, `AddInventory`.
- [ ] `dotnet-ef` is on `PATH` (`dotnet ef --version` prints 10.x).
- [ ] `podman-compose config` validates and lists `postgres` **and** `minio`.
- [ ] `Acentra.Domain` has **zero** package references to EF Core or ASP.NET.
- [ ] `.gitignore` ignores `storage/`.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- `Acentra.Domain` must not reference EF Core, ASP.NET Core, or Npgsql.
- No tenant-owned entity may lack a non-nullable `Guid TenantId`.
- `Program.cs` pipeline order must be: `UseHttpsRedirection` → `UseAuthentication` → `TenantResolutionMiddleware` → `UseAuthorization` → `UseAntiforgery` → `MapRazorComponents`.

**Non-Goals:** real EF model configuration, migrations, middleware logic, storage logic, UI, tests, README/architecture edits.

## 8. Edge Cases and Failure Behavior

- `dotnet-ef` install fails (offline) → report as a blocker; do not fake migrations.
- A package resolves to a non-10.x version → stop and report the resolved version table rather than forcing a pin.

## 9. Existing Behavior That Must Remain Unchanged

- Solution file, project list, and reference graph unchanged.
- Blazor template pages (Home, Counter, Weather, Error, NotFound) still render.
- `MapStaticAssets()` stays.

## 10. Dependencies and Assumptions

- Rootless `podman` works on this host (verified).
- No AWS account: S3 access is MinIO-only.
- Downstream plans depend on the exact names/signatures in §4 — **do not rename** them.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. All five architecture questions are answered: ASP.NET Core Identity · MinIO locally · gitignored `storage/` in-repo · **database-per-tenant** · Blazor.

## 12. Validation Commands

```bash
dotnet restore Acentra.slnx
dotnet build Acentra.slnx
dotnet test Acentra.slnx
dotnet ef --version
podman-compose config
```

## 13. Expected Final State

Solution builds green with all contracts present, MinIO in `compose.yaml`, `storage/` gitignored, `dotnet-ef` available, and `Program.cs` wired to four module registrations whose bodies later plans fill in.

## 14. Version Control / Checkpoint Strategy

- Checkpoint commit before starting: `checkpoint: pre-REQ-001`.
- Final commit after Overseer verification: `feat(foundation): domain entities, contracts, DI skeleton, MinIO, tooling`.

## 15. Agent Instructions / Execution Rules

1. Implement only §4/§5. Do not write EF configurations, migrations, middleware logic, or UI.
2. The contracts in §4 are frozen — later plans are parallelised against them.
3. Stubs must compile. Never leave a TODO or a placeholder that hides a missing type.
4. Stop and report if a package cannot be resolved for `net10.0`.
