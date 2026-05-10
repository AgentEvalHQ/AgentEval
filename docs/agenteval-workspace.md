# The `.agenteval/` Workspace

> **Reference** for the canonical AgentEval output layout — the standard for
> how runs, baselines, compliance evidence, and red-team artefacts are
> persisted on disk.

`.agenteval/` is AgentEval's single source of truth for evaluation runs,
baselines, compliance evidence, and red-team campaigns. Every run is identified
by a UUID and protected by a SHA-256 content hash; every compliance attestation
is cryptographically tied to a specific run; every subject (agent or workflow)
has a stable folder derived deterministically from its display name.

The layout is consumed read-only by Mission Control (the local viewer/portal —
see [Mission Control Getting Started](missioncontrol/getting-started.md)) and
written by the CLI, the test harnesses, and the benchmark runners.

---

## Bootstrap

Run `agenteval init` once per repository. It walks up from the current
directory to find a `.sln`, `.slnx`, or `.git` marker, treats that as the
workspace root, and creates `.agenteval/` if it does not exist.

```bash
agenteval init
agenteval init --name "My Solution"   # set a display name explicitly
```

Three files are written:

- **`solution.json`** — random UUID (`id`), display name (`name`), and
  `schemaVersion: "1.0"`. The stable identity anchor.
- **`README.md`** — human-readable overview of the layout.
- **`.gitignore`** — excludes per-run artefacts (`subjects/*/*/runs/`), the
  runs index, and red-team outputs from source control. Baselines and
  compliance evidence are not excluded.

If `.agenteval/solution.json` already exists, `agenteval init` exits cleanly
without overwriting anything.

---

## Canonical layout

```
.agenteval/
├── solution.json                 # workspace identity (UUID + name)
├── README.md                     # human-readable overview
├── .gitignore                    # excludes per-run artefacts
│
├── config/                       # workspace-wide settings + thresholds
│   ├── settings.json
│   └── thresholds/
│       └── <subject>.json
│
├── subjects/                     # one folder per agent / workflow
│   ├── agents/<Name>/
│   │   ├── subject.json
│   │   ├── baseline.json         # latest baseline (convenience copy)
│   │   ├── baselines/            # pinned, versioned baselines
│   │   │   └── v1.json
│   │   ├── history.jsonl         # append-only score history
│   │   └── runs/<runId>/
│   │       ├── manifest.json     # run identity + content hash
│   │       ├── summary.json      # aggregated scores
│   │       ├── scenarios/        # per-scenario results
│   │       │   └── <id>.json
│   │       ├── traces/
│   │       │   └── agent-trace.json
│   │       └── reports/
│   │           └── report.md / .html / .pdf / .junit.xml / .sarif
│   └── workflows/<Name>/         # same shape as agents/
│
├── compliance/                   # cryptographically chained to runs
│   └── <regulation>/<subject>/<ts>/
│       ├── evidence.json         # ComplianceEvidence (audit-chained)
│       ├── gdpr-evidence.json    # regulation-specific wrapper
│       └── report.pdf
│
├── benchmarks/                   # agentic benchmark output
│   └── agentic/<subject>/<ts>/
│       ├── agentic-result.json
│       ├── report.md
│       └── report.pdf
│
├── red-team/<campaign>_<ts>/     # red-team campaigns
│   ├── manifest.json
│   ├── findings.json
│   └── reports/
│
├── runs-index/                   # cross-cutting indices
│   ├── recent.jsonl              # most recent N runs
│   └── runs.index.jsonl          # master index
│
├── projects/                     # eval projects
│   └── <Project>/
│       ├── project.json
│       └── declares.jsonl
│
└── portal/                       # Mission Control sync state
    ├── targets.json
    ├── outbox.jsonl
    └── synced.jsonl
```

The canonical accessor for every path above is `FileSystemLayout` in
`src/AgentEval.DataLoaders/Output/FileSystemLayout.cs`. Mission Control's REST
endpoints (`/api/v1/runs/{runId}/trace`, `/api/v1/runs/{runId}/reports/{format}`,
`/api/v1/compliance/{reg}/{subject}/{ts}/report.pdf`) resolve paths through
this helper so the layout convention is shared.

Untrusted route segments (`runId`, `regulation`, `timestamp`, `format`) are
strictly validated via `FileSystemLayout.IsSafePathSegment` before being
combined into a filesystem path. This rejects directory traversal, control
chars, Windows reserved device names, NFKC-equivalent lookalikes, and
zero-width characters.

---

## Schema versions

Core v1 schemas are embedded as resources in the `AgentEval.DataLoaders`
assembly and loaded at runtime with no filesystem dependency. Benchmark and
regulation-specific schemas live alongside their owning project.

| Schema | Document | Resource location |
|---|---|---|
| `solution.schema.json` | `solution.json` | `AgentEval.DataLoaders` |
| `subject.schema.json` | `subjects/*/<name>/subject.json` | `AgentEval.DataLoaders` |
| `manifest.schema.json` | `runs/<runId>/manifest.json` | `AgentEval.DataLoaders` |
| `summary.schema.json` | `runs/<runId>/summary.json` | `AgentEval.DataLoaders` |
| `history-line.schema.json` | `subjects/*/<name>/history.jsonl` (per line) | `AgentEval.DataLoaders` |
| `evidence.schema.json` | `compliance/<reg>/<subject>/<ts>/evidence.json` | `AgentEval.DataLoaders` |
| `eval-result.schema.json` | Embedded recursive `EvalResult` trees | `AgentEval.DataLoaders` |
| `red-team-manifest.schema.json` | `red-team/<campaign>_<ts>/manifest.json` | `AgentEval.DataLoaders` |
| `evaluator-card.schema.json` | EvaluatorCard JSON files | `AgentEval.DataLoaders` |
| `agentic-result.schema.json` | `benchmarks/agentic/.../agentic-result.json` | `AgentEval.Evals.Agentic` |
| `gdpr-evidence.schema.json` | `compliance/GDPR/.../gdpr-evidence.json` | `samples/AgentEval.GdprBenchmark` |
| `eu-ai-act-evidence.schema.json` | `compliance/EU-AI-Act/.../eu-ai-act-evidence.json` | `samples/AgentEval.EuAiActBenchmark` |

Future schema bumps are additive (new optional fields only) until a v2 is
declared. The `schemaVersion` field selects the correct validator when
multiple versions are in play.

---

## Audit chain

Compliance evidence is stored at
`.agenteval/compliance/{regulation}/{subject}/{timestamp}/evidence.json`. Each
evidence document carries a `sourceRun` block with the originating `runId` and
that run's `manifestHash`.

When `SaveComplianceEvidenceAsync` writes evidence, the store:

1. Validates the evidence document against `evidence.schema.json`.
2. Locates the source run's `manifest.json`.
3. Compares `sourceRun.manifestHash` to the manifest's recorded hash.
4. Refuses the write on mismatch.

This means you cannot attach an attestation to a run whose artefacts were
modified after completion. `ContentHasher.HashRunAsync` covers the run's
summary, sorted scenario results, embedded `EvalResult` trees, and optional
trace.

### What the chain guarantees — and what it doesn't (v1)

The v1 audit chain enforces a single equality: an evidence document's
`sourceRun.manifestHash` field must equal the manifest's stored
`contentHash` field. Both are read from disk; neither is recomputed at
verification time. This catches the common tampering vector — editing a
run's `contentHash` field after sealing — and is what Mission Control's
"Source-run hash verified" badge reports.

Three weaker guarantees the v1 chain does NOT enforce:

1. **The manifest's `contentHash` is not recomputed against its body.** An
   attacker who edits the manifest's `subject.name`, `git.commit`, `seed`,
   or `evalProject` fields without touching `contentHash` will pass the
   chain check. Re-running `agenteval doctor` re-hashes the run's summary
   + scenarios + trace via `ContentHasher.HashRunAsync` and catches
   tampering of THOSE files, but the manifest itself is currently trusted
   as a label.
2. **The evidence document body is not hashed.** Edits to
   `controls[i].status`, `controls[i].passRate`, or the `attestation`
   block change the evidence semantics but do not change any hash
   compared by the chain. Evidence integrity in v1 depends on filesystem
   ACLs and the integrity of the writing process.
3. **No cross-evidence chain.** Each evidence document points back to one
   run; there is no "previous evidence hash" pointer that would let you
   reconstruct a tamper-evident timeline of attestations for a single
   subject.

These are tracked as v2 hardening (canonical-JSON hashing across manifest
+ evidence; chained evidence hashes). For v1 the chain is the right
defence against the most common tampering vector — direct edits to a
run's stored hash — and Mission Control's badge wording reflects what is
actually enforced.

### `agenteval doctor`

Re-validates the entire chain on demand:

```bash
agenteval doctor
```

For every evidence file, doctor:

1. Reads `sourceRun.runId` and `sourceRun.manifestHash`.
2. Locates the corresponding `manifest.json` under
   `subjects/{agents|workflows}/{name}/runs/{runId}/`.
3. Compares the stored `contentHash` with the value in the evidence file.
4. Reports a `Hash mismatch` error if the values differ.

```
solution.json OK
Run 3f8a1b2c (subject: TravelAgent)
Run 7d9e4f01 (subject: TravelAgent)
compliance/GDPR/TravelAgent/2026-04-10_14-32-00/evidence.json

Errors: 0 | Warnings: 0 | OK: 3
```

Exit code is `2` when any errors are reported, `0` when the workspace is
clean. Run it in CI to catch tampering or accidental overwrite before
publishing compliance reports.

> The audit chain catches the two most common accidental-corruption patterns
> ("did you forget to update evidence after re-running?" and "is this evidence
> consistent with the run it cites?"). For cryptographic anti-tampering
> against a determined attacker, sign the evidence files externally using your
> organisation's key-management infrastructure.

---

## Read-only consumption from Mission Control

Mission Control consumes `.agenteval/` strictly through `IOutputStoreReader`
(the read-only interface in `src/AgentEval.Abstractions/Output/`). A
reflection-based test (`ReaderOnlyArchitectureTests`) verifies on every build
that no `AgentEval.MissionControl` type references `IOutputStore` — the write
surface is unreachable from the portal binary.

A read-only Docker bind-mount preserves this guarantee end-to-end:

```bash
docker run --rm -p 5000:5000 \
  -v "$(pwd)/.agenteval:/workspace/.agenteval:ro" \
  agenteval/mc:latest
```

---

## Output store modes

`AddAgentEvalOutputStore` is the entry point. Three modes are available via
`OutputStoreOptions.OutputStore`:

| Mode | Behaviour |
|---|---|
| `Auto` (default) | Uses the file-system store when a workspace root with `solution.json` is discoverable; otherwise returns `NullOutputStore`. The standard registration for production code. |
| `FileSystem` | Always uses the file-system store. Throws if no workspace root is found — useful when you want a hard failure rather than silently dropping data. |
| `Null` | Always uses `NullOutputStore`. Accepts every write call and discards the data silently; no `.agenteval/` folder is touched. Pick this in unit tests and contexts where filesystem side effects are not acceptable. |

```csharp
// Tests / no-op
services.AddAgentEvalOutputStore(opts =>
    opts.OutputStore = OutputStoreMode.Null);

// Force file-system, fail loudly if the workspace is missing
services.AddAgentEvalOutputStore(opts =>
    opts.OutputStore = OutputStoreMode.FileSystem);
```

If `AddAgentEvalOutputStore` is never called, `IOutputStore` is not
registered and DI resolution fails for any code that depends on it.

---

## See also

- [Mission Control Getting Started](missioncontrol/getting-started.md) — read-only portal that consumes `.agenteval/`.
- [Composite Evaluations](composite-evals.md) — recursive `EvalResult` persistence inside `subjects/*/runs/{runId}/scenarios/`.
- [CLI Reference](cli.md) — `agenteval init`, `agenteval doctor`.
