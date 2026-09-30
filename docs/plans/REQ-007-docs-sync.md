# REQ-007 — Documentation sync with the locked decisions

**Risk Level:** Low — documentation only. No code.

> **Execution order:** runs **last, alone**, after REQ-002…REQ-006 are merged. It is sequential on purpose: every earlier plan touches these same files, so they cannot be edited in parallel.

## 1. Exact Feature Request and Clarified Requirements

The decision log changed, and `docs/architecture.md` still records the old answers. Bring every document in line with reality:

| Decision | Was | Now |
|---|---|---|
| Auth provider | open (§11) | **ASP.NET Core Identity** |
| File storage | local FS, MinIO open (§6, §11) | **MinIO locally, S3-compatible** |
| Storage root | open (§11) | **gitignored `storage/` inside the repo** |
| Database topology | **shared schema + `TenantId`** (§5.4) | **database per tenant**, with `TenantId` + global query filters retained as defence in depth |
| Frontend | Blazor (locked §8) | **Blazor** (unchanged); real frontend delivered later by another team, placeholder UI for now |

## 2. Scope

- **Allowed Files/Directories:** `docs/architecture.md`, `docs/problem-statement.md`, `README.md`, `docs/plans/**` (only to correct statements that have become factually wrong).
- **In-Scope:** decision records, status tables, requirement traceability, environment instructions, the new plans index.
- **Out of Scope:** any code, any new design proposal.

## 3. Current Architecture / Context

The three docs were written when the database decision was **shared schema** and auth was undecided. `REQ-003` implements database-per-tenant *while keeping* the global query filters, so §5.4's decision row and its "right for" reasoning are now inverted, and §5.2/§5.3 remain correct but need a sentence explaining they now sit on top of physical separation.

## 4. Implementation Requirements & Interfaces

- **`docs/architecture.md`**
  - §0 Status — reflect what is actually built, per requirement, at the time of writing.
  - §5.1–5.4 — database-per-tenant is the decision; `TenantId` + global query filters are retained explicitly to satisfy requirement #3 and to make a connection-routing bug non-leaking. Update the comparison table's verdict.
  - §5.6 — add the per-tenant-database consequence: `CREATE DATABASE` is outside filter reach and lives only in `TenantProvisioner`.
  - §6 — MinIO locally; `storage/` gitignored; both providers share one key layout.
  - §9 stack table — Identity, MinIO, per-tenant DBs.
  - §11 — move the five answered questions from **Open** to **Locked**; remove the stale ones.
  - Add a **Plans** section linking `docs/plans/REQ-001…REQ-007`.
- **`docs/problem-statement.md`** — update the traceability matrix to the true status; rewrite the "notes on the two deviations" so #10 reads "MinIO locally = literally S3-compatible" rather than "no AWS account, local FS only".
- **`README.md`** — stack table (Identity, MinIO, per-tenant DBs), getting-started steps for both containers, the `dotnet-ef` prerequisite, and the plans index.
- Every claim must be **verified against the code**, not copied from the old text.

## 5. Step-by-Step Implementation Plan

1. Re-read `src/` and `tests/` and record the actual state per requirement (done / not done).
2. Update `architecture.md` sections listed in §4.
3. Update the traceability matrix in `problem-statement.md`.
4. Update `README.md`.
5. Reconciled the plans directory: add the index, and correct any plan statement contradicted by what was implemented (do not rewrite plan intent).
6. Validate: every claim in the docs has a matching file/command in the repo.

## 6. Acceptance Criteria & Testing Requirements

- [ ] No document claims shared schema, an open auth decision, or an open MinIO decision.
- [ ] §11 contains **no** answered question marked open.
- [ ] Every ✅ in the traceability matrix can be pointed at a real file.
- [ ] `README.md` instructions reproduce a working local environment from a clean checkout.
- [ ] Docs index links to all seven plans and the links resolve.

## 7. Boundaries: Constraints & Non-Goals

**Constraints/Invariants:** documentation must describe what exists, not what is planned; no aspirational ✅.

**Non-Goals:** rewriting the architecture rationale, adding new designs, creating new plan files for future work.

## 8. Edge Cases and Failure Behavior

- A requirement that is *partially* met → mark `🔨` with a one-line note, never ✅.
- A plan statement contradicted by implementation → fix the plan text to match reality and note the deviation; do not "fix" the code in this plan.

## 9. Existing Behavior That Must Remain Unchanged

- Document tone, structure and heading numbering remain recognisable.
- The existing tables stay tables; extend them rather than replacing them wholesale.

## 10. Dependencies and Assumptions

- REQ-001…REQ-006 are merged and green.
- The five decisions in §1 are final.

## 11. Decision Points / Prohibited Autonomous Decisions

**UNRESOLVED DECISIONS:** none.

## 12. Validation Commands

```bash
grep -rn "shared schema" docs/ README.md
grep -rn "Open" docs/architecture.md
dotnet test Acentra.slnx
```

## 13. Expected Final State

The three documents and the plans directory describe the platform as it actually is: Blazor + Identity + database-per-tenant + global query filters + MinIO-backed storage, with an accurate status table and an index of the seven plans.

## 14. Version Control / Checkpoint Strategy

- Checkpoint before starting: `checkpoint: pre-REQ-007`.
- Final commit: `docs: sync architecture, problem statement and README with locked decisions`.

## 15. Agent Instructions / Execution Rules

1. Documentation only — do not touch `src/` or `tests/`.
2. Verify every statement against the code before writing it down.
3. Never mark a requirement ✅ unless the implementing file exists and was read.
