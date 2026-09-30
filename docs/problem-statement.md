# .NET Problem Statement: Multi-Tenant Inventory Platform

**Difficulty:** Hard

## Overview

Multi-tenant applications must ensure that one organization's data can never be accessed by another organization. Tenant isolation should be enforced at the architecture and data-access levels.

## Minimum Requirements

| # | Requirement |
| --- | --- |
| 1 | ASP.NET Core application |
| 2 | Entity Framework Core |
| 3 | Global query filters for tenant isolation |
| 4 | Middleware to identify the current tenant |
| 5 | Support tenant identification using a header, subdomain, or similar approach |
| 6 | Inventory management per tenant |
| 7 | React or Blazor frontend |
| 8 | Ability to switch tenant context |
| 9 | Display only the selected tenant's inventory |
| 10 | Store tenant-specific files in S3 |
| 11 | Ensure tenant data isolation |

---

## How each requirement is being met

Status key: ✅ done · 🔨 in progress · ⏳ not started

| # | Requirement | Approach | Where | Status |
| --- | --- | --- | --- | --- |
| 1 | ASP.NET Core application | Blazor Web App as the single ASP.NET Core host (`Acentra.Web`) | `src/Acentra.Web` | ✅ |
| 2 | Entity Framework Core | EF Core 10 with `Npgsql.EntityFrameworkCore.PostgreSQL`; one context per unit of work | `src/Acentra.Infrastructure` | ⏳ |
| 3 | Global query filters | `HasQueryFilter` on every tenant-owned entity, plus a `SaveChanges` interceptor for the write path | architecture §5.2–5.3 | ⏳ |
| 4 | Tenant middleware | `TenantResolutionMiddleware` resolving the tenant and publishing a scoped `ITenantContext` | architecture §4 | ⏳ |
| 5 | Tenant identification | Header `X-Tenant` by default; subdomain and JWT claim supported via `ITenantResolver` | architecture §4 | ⏳ |
| 6 | Inventory per tenant | `Product`, `StockLevel`, `StockMovement` entities, all carrying `TenantId` | architecture §5.1 | ⏳ |
| 7 | Blazor frontend | Blazor Web App, interactive Server render mode, all-interactive | architecture §8 | ✅ |
| 8 | Switch tenant context | Circuit-scoped `TenantState` mutated by the UI switcher | architecture §4.1, §7 | ⏳ |
| 9 | Show only selected tenant's inventory | Selection drives every query; switching clears cached component state | architecture §5.2, §7 | ⏳ |
| 10 | Tenant files in S3 | **Deviation:** no AWS account, so files go to a local filesystem behind `IFileStorage`; an S3/MinIO implementation is a drop-in | architecture §6, §11 | ⏳ |
| 11 | Tenant data isolation | Defense in depth: middleware → scoped context → query filters → write interceptor → isolation test matrix | architecture §5 | ⏳ |

### Notes on the two requirements we deviate from

- **#7 React or Blazor** — the statement allows either; Blazor was chosen (§8 of the architecture doc records the comparison and the reasoning).
- **#10 S3** — no AWS account is available. The requirement is satisfied structurally rather than literally: all file access goes through `IFileStorage`, whose local implementation uses the same `tenants/{tenantId}/…` key layout an S3 bucket would. Running MinIO locally would make it literally S3 API-compatible; that is an open decision.

---

Companion document: [`architecture.md`](./architecture.md).
