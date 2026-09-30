# Acentra

A multi-tenant inventory platform built on ASP.NET Core + EF Core, with a Blazor Web App frontend.

Tenant isolation is enforced at the architecture and data-access layers — not in UI code.

## Docs

- [Problem statement](./docs/problem-statement.md) — requirements
- [Architecture](./docs/architecture.md) — layers, tenant resolution, EF Core global query filters, S3 layout, frontend decision

## Stack

| Layer | Choice |
|---|---|
| Runtime | .NET (LTS) |
| API | ASP.NET Core |
| ORM | EF Core — one scoped `DbContext`, global query filters + `SaveChanges` interceptor |
| Tenant resolution | Middleware + `ITenantResolver` (header / subdomain / JWT claim) |
| Database | TBD |
| Files | S3, `tenants/{tenantId}/…`, presigned URLs |
| Frontend | Blazor Web App, interactive Server render mode |
| Tests | xUnit — includes a tenant-isolation test matrix |

## Status

Planning. See the open decisions in §10 of the architecture doc.

## Getting started

Requires the .NET SDK. On Arch/CachyOS:

```bash
sudo pacman -S dotnet-sdk aspnet-runtime aspnet-targeting-pack
```

### Local database (rootless podman)

```bash
podman-compose up -d          # Postgres 17 on localhost:5432
podman-compose ps             # wait for "healthy"
podman-compose down           # stop, keep data
podman-compose down -v        # stop and wipe data
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
