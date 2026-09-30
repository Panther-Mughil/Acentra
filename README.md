# Acentra

A multi-tenant inventory platform built on ASP.NET Core + EF Core, with a Blazor Web App frontend.

Tenant isolation is enforced at the architecture and data-access layers — not in UI code.

## Docs

- [Problem statement](./docs/problem-statement.md) — the brief, plus a requirement-by-requirement traceability matrix
- [Architecture](./docs/architecture.md) — solution layout, tenant resolution, EF Core global query filters, Blazor circuit scoping, file storage, frontend decision

## Stack

| Layer | Choice |
| --- | --- |
| Runtime | .NET 10 (`net10.0`), SDK `10.0.112` |
| Solution format | `Acentra.slnx` (.NET 10 XML solution) |
| Host | Blazor Web App, interactive Server, all-interactive — single host |
| Data access | EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` |
| Database | PostgreSQL 17 (`postgres:17-alpine`), shared schema + `TenantId` |
| Tenant resolution | `TenantResolutionMiddleware` + `ITenantResolver`, header `X-Tenant` by default |
| Isolation | Tenant-scoped schema, EF Core global query filters, `SaveChanges` interceptor |
| Files | Local filesystem behind `IFileStorage` (no AWS account) — S3/MinIO drop-in |
| Frontend | Blazor Web App, interactive Server render mode |
| Tests | xUnit — unit tests + a tenant-isolation integration matrix |

## Status

Scaffold complete and pushed. Design is settled; feature work is next.

| Area | State |
| --- | --- |
| 5 projects + solution, wired references | ✅ |
| `dotnet build` / `dotnet test` | ✅ 0 warnings, 2/2 passing |
| Postgres 17 via rootless podman | ✅ verified accepting connections |
| Domain entities, EF Core + filters, tenant middleware | ⏳ next |
| Inventory UI, tenant switcher, file storage, auth | ⏳ |

See §0 of the [architecture doc](./docs/architecture.md) for the detailed status and §11 for open decisions.

## Repo layout

```text
Acentra.slnx                     solution
compose.yaml                     rootless podman Postgres for local dev
docs/
src/
├─ Acentra.Domain/               entities — no EF Core / ASP.NET references
├─ Acentra.Infrastructure/       AppDbContext, EF config, migrations, storage, resolvers
└─ Acentra.Web/                  the single host (Blazor Web App + middleware)
tests/
├─ Acentra.UnitTests/
└─ Acentra.IntegrationTests/     isolation matrix via WebApplicationFactory
```

## Getting started

Toolchain on Arch/CachyOS. The SDK package deliberately excludes ASP.NET Core, so all three are required — without the last two, every web project fails with `NETSDK1226: Prune Package data not found .NETCoreApp 10.0 Microsoft.AspNetCore.App`:

```bash
sudo pacman -S dotnet-sdk aspnet-runtime aspnet-targeting-pack
```

### Local database (rootless podman)

```bash
podman-compose up -d          # Postgres 17 on localhost:5432
podman-compose ps             # wait for "healthy"
podman-compose down           # stop, keep data
podman-compose down -v        # stop and WIPE the database
```

Connection string used by development:

```text
Host=localhost;Port=5432;Database=acentra;Username=acentra;Password=acentra_dev_password
```

Override via a gitignored `.env` next to `compose.yaml`:

```bash
POSTGRES_PORT=5433
POSTGRES_PASSWORD=something_else
```

### Build and test

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
```

## Repository

- Remote: `git@github.com:Panther-Mughil/Acentra.git` (SSH), branch `main`
- Pushes go over SSH. The `gh` CLI has no stored session on this machine and its auth flow needs an interactive terminal, so it is not used.
