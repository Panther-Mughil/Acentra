# REQ-004 — File storage: `IFileStorage`, local filesystem, S3/MinIO, tenant file metadata

**Risk Level:** Medium — self-contained module behind a frozen interface, but path traversal and cross-tenant key reuse are real security risks.

> **Execution order:** runs **in parallel** with REQ-002 and REQ-003, after REQ-001. Owns `src/Acentra.Infrastructure/Storage/**`, the `TenantFile` configuration, and the `minio` service in `compose.yaml`.

## 1. Exact Feature Request and Clarified Requirements

Requirement #10 says tenant files go to S3. There is no AWS account, so:

- **MinIO runs locally** (decision #2) — the code path is genuinely S3 API compatible, so requirement #10 is met literally, not by analogy.
- A **local filesystem** implementation is kept as a second provider so tests and offline runs need no MinIO.
- Both implementations share **one key layout** — `tenants/{tenantId}/{category}/{guid}{ext}` — so a provider swap changes no caller and no database row.
- `storage/` lives **inside this repository and is gitignored** (decision #3).
- File **metadata** (key, name, content type, size, upload time) is stored in the **tenant database** via the existing `TenantFile` entity; only the key travels to S3.

## 2. Scope

- **Allowed Files/Directories:** `src/Acentra.Infrastructure/Storage/**`, `compose.yaml` (minio service only), `.gitignore` (`storage/` + `.pi-lens-probe-home/`), `src/Acentra.Web/appsettings*.json` (`Storage` section).
- **NOT your files — do not edit:** `src/Acentra.Infrastructure/TenantData/**` (REQ-003 owns it, **including** `TenantData/Configurations/TenantFileConfiguration.cs`, which is already written and migrated), `src/Acentra.Infrastructure/ControlPlane/**`, `src/Acentra.Infrastructure/Inventory/**`, `src/Acentra.Web/**`, `Migrations/**`.
- **In-Scope:** `IFileStorage` implementation(s), `StorageOptions` binding, key builder, filename sanitiser, provider selection, and the `minio` service in `compose.yaml`.
- **Out of Scope:** the `TenantFile` EF configuration (REQ-003, already done), the Blazor upload UI (REQ-005 wires a page to the service), query filters / context plumbing (REQ-003), auth (REQ-002).

**Expected File/Component Changes:**

| File | Expected Change | Reason |
| --- | --- | --- |
| `src/Acentra.Infrastructure/Storage/LocalFileStorage.cs` | Add | filesystem provider |
| `src/Acentra.Infrastructure/Storage/S3FileStorage.cs` | Add | MinIO/S3 provider |
| `src/Acentra.Infrastructure/Storage/StorageKeyBuilder.cs` | Add | shared key layout + sanitisation |
| `src/Acentra.Infrastructure/Storage/StorageOptions.cs` | Add | bound configuration |
| `src/Acentra.Infrastructure/Storage/ServiceCollectionExtensions.cs` | Complete stub | `AddFileStorage` provider switch |
| `src/Acentra.Infrastructure/TenantData/Configurations/TenantFileConfiguration.cs` | **REQ-003 owns this** | unique `(TenantId, Key)` — already implemented and migrated; do not touch |
| `compose.yaml` | Modify | `minio` service + volume |
| `.gitignore` | Verify `storage/` | keep uploads out of git |
| `src/Acentra.Web/appsettings*.json` | Modify | `Storage` defaults for dev |

## 3. Current Architecture / Context

- REQ-001 defines `IFileStorage` in `Acentra.Domain/Abstractions` and the `Storage` section in `appsettings.json`.
- `TenantFile` (REQ-001) is tenant-owned: `Id, TenantId, Key, FileName, ContentType, SizeBytes, UploadedUtc`.
- REQ-003 supplies the tenant database where `TenantFile` rows live and the global query filter that protects them.
- The `Storage` DI extension is called from `Program.cs` in REQ-001 — this plan only fills the body.

## 4. Implementation Requirements & Interfaces

The `IFileStorage` signature is **frozen** (REQ-001, architecture §6):
```csharp
Task<string> SaveAsync(Guid tenantId, Stream content, string fileName, CancellationToken ct);
Task<Stream> OpenAsync(Guid tenantId, string key, CancellationToken ct);
Task DeleteAsync(Guid tenantId, string key, CancellationToken ct);
Task<string> GetDownloadUrlAsync(Guid tenantId, string key, TimeSpan ttl, CancellationToken ct);
```

```csharp
public sealed class StorageOptions {
    public string Provider { get; set; } = "S3";   // "S3" | "Local"
    public string LocalRoot { get; set; } = "storage";
    public string Endpoint { get; set; } = "http://localhost:9000";
    public string Bucket { get; set; } = "acentra-tenant-files";
    public string AccessKey { get; set; } = "acentra";
    public string SecretKey { get; set; } = "acentra_dev_password";
    public bool UseSsl { get; set; }
    public string Region { get; set; } = "us-east-1";
}
```

**Key layout (identical for both providers):** `tenants/{tenantId:N}/{category}/{guid:N}{ext}`, `category` ∈ `{images, documents, misc}`, default `misc`. `tenantId` is **always** taken from the resolved tenant, never from the request body.

**Local provider:** resolves `<LocalRoot>/tenants/...`, creating directories as needed. After combining the path it must verify `Path.GetFullPath(result).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar)` — reject `..`, absolute paths, and symlink escapes. `GetDownloadUrlAsync` returns a tokenised relative app URL rather than a presigned URL, keeping the contract shape.

**S3 provider (MinIO):**
- `AmazonS3Client` with `ServiceURL = Endpoint`, `ForcePathStyle = true` (**required** for MinIO), `AuthenticationRegion = Region`, and `UseHttp = !UseSsl`.
- Create the bucket on startup if absent (`DoesS3BucketExistV2Async` → `PutBucketAsync`).
- MinIO compatibility: when payload signing causes `SignatureDoesNotMatch`, set `DisablePayloadSigning = true` on the put request. Verify with a real round-trip rather than assuming.
- `SaveAsync`/`OpenAsync`/`DeleteAsync` map to `PutObjectAsync`/`GetObjectAsync`/`DeleteObjectAsync`; `GetDownloadUrlAsync` returns `GetPreSignedURL` with the given TTL.
- Key prefix (`tenants/{tenantId}/...`) must be re-verified before every read — a key from another tenant must never be opened.

**Provider selection:** `Provider` = `S3` (default in Development) or `Local`; integration tests inject `Local` so no MinIO is required.

**`compose.yaml` addition:** `minio` service, command `server /data --console-address ":9001"`, ports `9000` and `9001` (both env-overridable), `MINIO_ROOT_USER`/`MINIO_ROOT_PASSWORD` from env with dev defaults, named volume `acentra-minio-data`, healthcheck `curl -f http://localhost:9000/minio/health/live`.

> **Image source — this is your first task, and REQ-001 landed it wrong.** REQ-001 shipped `docker.io/minio/minio`, which **cannot be pulled**: upstream MinIO has withdrawn its Docker Hub images, so every tag returns `requested access to the resource is denied` (`quay.io/minio/minio` also returns `unauthorized`). Change the image to **`docker.io/pgsty/minio:latest`** — a community mirror of the same MinIO server, verified by the Overseer to serve `HTTP 200` on `/minio/health/live` at `RELEASE.2026-08-04`. Do not switch to a different S3 product (SeaweedFS/Garage/LocalStack) — decision #2 is MinIO specifically. `curl`, `mc` and `bash` are present in this image, so the healthcheck above is valid.
>
> `podman-compose config` does **not** catch this class of error — it only parses YAML. The container must actually be started and observed healthy.

## 5. Step-by-Step Implementation Plan

1. `StorageOptions` + configuration binding + `StorageKeyBuilder` (sanitiser, extension whitelist, GUID name).
2. `LocalFileStorage` with the traversal guard and directory creation.
3. `S3FileStorage` with path-style addressing, bucket bootstrap, presigned URLs.
4. Complete `AddFileStorage`: bind options, register the chosen provider as `IFileStorage` (scoped or singleton — options are immutable, so singleton is correct).
5. `TenantFileConfiguration` — `Key` required, unique `(TenantId, Key)`, index `(TenantId, UploadedUtc)`.
6. Add the `minio` service to `compose.yaml`; add `Storage` defaults to `appsettings.Development.json`.
7. `podman-compose up -d`, then prove a real round-trip: put an object, list it, presign, fetch, delete — via a scripted check (a small xUnit test or a `curl`/`mc` verification).
8. `dotnet build` + `dotnet test`.

## 6. Acceptance Criteria & Testing Requirements

- [ ] `dotnet build Acentra.slnx` → 0 warnings, 0 errors; `dotnet test` → 2/2 plus new storage tests.
- [ ] `podman-compose config` validates; `podman-compose up -d` brings up `postgres` **and** `minio`; **both report `(healthy)` in `podman-compose ps`** — observed, not inferred. The image reference is `docker.io/pgsty/minio:latest`.
- [ ] MinIO round-trip verified with the **real** client: `SaveAsync` then `OpenAsync` returns byte-identical content; `DeleteAsync` removes it.
- [ ] `LocalFileStorage` round-trips identically against a temp root.
- [ ] A key containing `..` or an absolute path is **rejected**, and nothing is written outside `LocalRoot`.
- [ ] The same `StorageKeyBuilder` output works unchanged against both providers (byte-identical content, same key string).
- [ ] `GetDownloadUrlAsync` for the S3 provider returns a presigned URL usable without extra credentials.
- [ ] `TenantFile` rows are unique per `(TenantId, Key)`.

**Required Tests / Demonstrated Behavior:** provider round-trip tests for both implementations, a traversal-rejection test, and a key-shape test.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:**
- The `IFileStorage` signature does not change — later plans and the frontend team build against it.
- `tenantId` is always server-resolved; a client-supplied tenid/key is never trusted verbatim.
- No AWS credentials, no real S3 endpoint, no `AWS_*` environment variables in the repo.
- `storage/` stays gitignored; never commit uploads.

**Non-Goals:** virus scanning, image resizing, multipart/large-file streaming, bucket policies, per-tenant buckets, CDN, lifecycle rules, the upload UI itself.

## 8. Edge Cases and Failure Behavior

- MinIO unreachable → `SaveAsync` fails with a clear exception; never a silent local fallback (that would hide a misconfiguration).
- Empty stream or zero-byte upload → allowed, `SizeBytes = 0`.
- Filename with no extension or a disallowed extension → store as `misc` with `ext` omitted.
- Bucket missing → created once at startup, idempotently.
- Presigned URL TTL ≤ 0 → clamp to a small positive default.
- Key that does not exist → `FileNotFoundException`-equivalent, not a null stream.

## 9. Existing Behavior That Must Remain Unchanged

- Postgres service definition and its volume.
- `.gitignore`'s existing rules; only the `storage/` line is added.
- No change to `Program.cs` (REQ-001 already calls `AddFileStorage`).

## 10. Dependencies and Assumptions

- REQ-001 froze the interface and added `AWSSDK.S3`; MinIO's API is compatible with path-style S3 access.
- `podman-compose up -d minio` works on this host.
- The `Storage` config section is the only switch between providers — no compile-time symbol.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none. MinIO locally (decision #2); gitignored `storage/` (decision #3).

## 12. Validation Commands

```bash
dotnet build Acentra.slnx
dotnet test Acentra.slnx
podman-compose config
podman-compose up -d && podman-compose ps
podman logs acentra-minio --tail 20
```

## 13. Expected Final State

MinIO running locally beside Postgres, `S3FileStorage` storing real objects under `tenants/{tenantId}/...`, `LocalFileStorage` as an offline/test alternative, and `TenantFile` metadata rows in the tenant database. Swapping providers changes one config value.

## 14. Version Control / Checkpoint Strategy

- Checkpoint once for the parallel batch: `checkpoint: pre-REQ-002..004`.
- Final commit: `feat(storage): IFileStorage with MinIO/S3 and local filesystem providers`.

## 15. Agent Instructions / Execution Rules

1. Stay inside §2. Do not touch `ControlPlane/`, `TenantData/` (except the one configuration file listed), `Inventory/`, or `.csproj`.
2. Do not add AWS SDK for anything except S3.
3. Do not weaken the traversal guard to make a test pass.
4. Report the exact MinIO round-trip evidence (command + output) — a green build alone does not prove S3 works.
