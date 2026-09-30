# .NET Problem Statement: Multi-Tenant Inventory Platform

**Difficulty:** Hard

## Overview

Multi-tenant applications must ensure that one organization's data can never be accessed by another organization. Tenant isolation should be enforced at the architecture and data-access levels.

## Minimum Requirements

| # | Requirement |
|---|-------------|
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
