# Migrating to the `.agenteval/` Workspace Layout

## Why we changed the layout

Before this release, AgentEval wrote output to three separate, uncoordinated locations: sample snapshots went to `.AgentEval/{Project}_Evals/{key}.json` (uppercase, project-specific); memory baselines went to `.agenteval/benchmarks/{Agent}/baselines/...`; and trace artifacts landed in `TestResults/traces/{name}_{ts}_{*}.json`. None of these locations shared a common identity layer, so there was no reliable way to correlate a trace with the run that produced it, no content hashes to detect corruption or tampering, and no audit trail linking a compliance attestation back to a specific evaluation run. The mixed casing (`.AgentEval/` vs `.agenteval/`) also caused false positives in tooling on case-insensitive filesystems.

The new `.agenteval/` workspace is the single source of truth. Every run is identified by a UUID and protected by a SHA-256 content hash. Every subject (agent or workflow) has a stable folder whose name is derived deterministically from the subject's display name. Every compliance attestation is cryptographically tied to a specific run: the evidence file records the run ID and the run's content hash, and both `SaveComplianceEvidenceAsync` and `agenteval doctor` refuse to accept a mismatch.

---

## What `agenteval init` does

Run `agenteval init` (or `agenteval init --name "My Solution"`) once per repository. The command walks up from the current directory until it finds a `.sln`, `.slnx`, or `.git` marker and treats that directory as the workspace root. It then creates `.agenteval/` if it does not exist and writes three files:

- **`solution.json`** — Records a random UUID (`id`), a display name (`name`), and `schemaVersion: "1.0"`. This file is the stable identity anchor for the entire workspace.
- **`README.md`** — A human-readable overview of the folder layout and a pointer to `agenteval doctor` and `agenteval migrate`.
- **`.gitignore`** — Excludes per-run artifacts (`subjects/*/*/runs/`), the runs index, and red-team campaign outputs from source control. Baselines and compliance evidence are not excluded.

If `.agenteval/solution.json` already exists, the command exits cleanly without overwriting anything.

---

## How to migrate existing data

### Memory baselines

No action required. `JsonFileBaselineStore` was updated with a dual-write constructor: when an `IOutputStore` and a `SubjectIdentity` are provided, every baseline save goes to both the legacy `.agenteval/benchmarks/{Agent}/baselines/` path (which remains the source of truth) and the canonical `.agenteval/subjects/agents/{Agent}/baselines/v{n}.json` path. Existing code that constructs `JsonFileBaselineStore` without an `IOutputStore` continues to use the legacy path only.

To move existing baseline files into the canonical layout, run:

```
agenteval migrate --dry-run
agenteval migrate --apply
```

### Traces (`TestResults/traces/`)

Run `agenteval migrate --apply`. The command parses the filename pattern `{name}_{yyyy-MM-dd}_{suffix}.json`, looks up a matching subject folder under `.agenteval/subjects/agents/{name}/`, and moves the file to `.agenteval/subjects/agents/{name}/runs/{ts}/traces/{original-filename}`. Files whose names do not match the expected pattern, or whose agent name does not correspond to a known subject folder, are skipped with a warning.

### Sample snapshots (`.AgentEval/ECS2026MAF_Evals/`)

The ECS2026MAF.Evals sample now writes to `.agenteval/samples/ECS2026MAF.Evals/snapshots/` automatically when you update to this release. For existing data you want to preserve, move it manually:

```
mv .AgentEval/ECS2026MAF_Evals .agenteval/samples/ECS2026MAF.Evals/snapshots
```

`agenteval migrate` will flag the `.AgentEval/` directory and suggest a manual move, but it does not move sample-specific sub-paths automatically because they do not map directly to subjects in the canonical layout.

### Example: `agenteval migrate --dry-run` output

```
[DRY-RUN] Rename .AgentEval → .agenteval (via temp intermediate on Windows).
[DRY-RUN] Move trace: TestResults/traces/TravelAgent_2026-04-10_run1.json
         → .agenteval/subjects/agents/TravelAgent/runs/2026-04-10_run1/traces/TravelAgent_2026-04-10_run1.json
[DRY-RUN] Move baseline: .agenteval/benchmarks/TravelAgent/baselines/baseline.json
         → .agenteval/subjects/agents/TravelAgent/baselines/v1.json
```

Pass `--apply` to execute the moves shown above.

### Example: `agenteval doctor` output (clean workspace)

```
✔ solution.json OK
✔ Run 3f8a1b2c (subject: TravelAgent)
✔ Run 7d9e4f01 (subject: TravelAgent)
✔ compliance/OWASP/TravelAgent/2026-04-10T14:32:00Z/evidence.json

Errors: 0 | Warnings: 0 | OK: 3
```

If a content hash mismatch is found:

```
✖ Hash mismatch in run 3f8a1b2c (subject: TravelAgent).

Errors: 1 | Warnings: 0 | OK: 2
```

Exit code is `2` when any errors are reported, `0` when the workspace is clean.

---

## How to disable the canonical store entirely

If you are not yet ready to adopt the new layout, do not call `AddAgentEvalOutputStore`. The framework falls back to legacy behavior automatically when no `IOutputStore` is registered.

To explicitly opt in to the no-op mode:

```csharp
services.AddAgentEvalOutputStore(opts =>
    opts.OutputStore = OutputStoreMode.Null);
```

`NullOutputStore` accepts all calls and discards all data silently. It is safe to use in unit tests or in contexts where filesystem side effects are not acceptable.

---

## Compliance audit chain

Compliance evidence is stored at `.agenteval/compliance/{regulation}/{subject}/{timestamp}/evidence.json`. Each evidence document contains a `sourceRun` block with the originating `runId` and the run's `manifestHash`.

When a compliance reporter calls `SaveComplianceEvidenceAsync`, the store validates the evidence document against `evidence.schema.json` and then looks up the source run's `manifest.json`. If `sourceRun.manifestHash` does not match the hash recorded in the manifest, the write is refused with an exception. This means you cannot attach an attestation to a run whose artifacts have been modified after the run completed.

`agenteval doctor` re-validates the entire audit chain on demand. For each evidence file it finds, it:

1. Reads `sourceRun.runId` and `sourceRun.manifestHash` from the evidence file.
2. Locates the corresponding `manifest.json` under `subjects/*/runs/{runId}/`.
3. Compares the stored `contentHash` with the value in the evidence file.
4. Reports a `✖ Hash mismatch` error if the values differ.

Run `agenteval doctor` in CI to catch any tampering or accidental overwrite before publishing compliance reports.

---

## Schema versions

All v1 schemas live as embedded resources in the `AgentEval.DataLoaders` assembly and are loaded at runtime without any filesystem dependency. Schema names: `manifest.schema.json`, `summary.schema.json`, `subject.schema.json`, `solution.schema.json`, `history-line.schema.json`, `evidence.schema.json`, `red-team-manifest.schema.json`.

Future schema bumps will be additive (new optional fields only) until a v2 is declared. The `schemaVersion` field in each document will be used to select the correct validator when multiple versions are in play.
