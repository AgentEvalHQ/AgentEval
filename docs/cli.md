# CLI Reference

AgentEval ships a CLI for managing the `.agenteval/` workspace from the terminal and CI/CD pipelines.

## Installation

```bash
# During development
dotnet run --project src/AgentEval.Cli -- <command>

# Packaged tool (planned)
dotnet tool install --global AgentEval.Cli --prerelease
```

After installation as a global tool, the `agenteval` command is available system-wide. Until the package is published, use `dotnet run --project src/AgentEval.Cli --` as the prefix in place of `agenteval`.

---

## Commands

### `agenteval init`

Initialize the `.agenteval/` workspace for the current solution.

**Synopsis**

```
agenteval init [--name <display-name>]
```

**What it does**

Walks up from the current directory until it finds a `.sln`, `.slnx`, or `.git` marker and treats that directory as the workspace root. Creates `.agenteval/` if it does not exist, then writes three files:

- `solution.json` — solution-level identity: a random UUID, the display name, and `schemaVersion: "1.0"`.
- `README.md` — overview of the workspace layout.
- `.gitignore` — excludes per-run artifacts and red-team outputs from source control.

If `solution.json` already exists, the command reports that the workspace is already initialized and exits cleanly.

**Options**

| Option | Description |
|--------|-------------|
| `--name <display-name>` | Display name to record in `solution.json`. Defaults to the directory name of the solution root. |

**Example**

```
$ dotnet run --project src/AgentEval.Cli -- init --name "MyProject"
✔ Initialized .agenteval/ at /home/user/myproject/.agenteval
```

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Initialized successfully (or already initialized). |
| `1` | Could not locate a solution root. |

---

### `agenteval doctor`

Validate the `.agenteval/` workspace structure and content hashes.

**Synopsis**

```
agenteval doctor
```

**What it does**

Performs five checks in sequence:

1. **`solution.json`** — Verifies that `schemaVersion`, `id` (non-empty GUID), and `name` are all present and well-formed.
2. **Subject-name consistency** — For each subject folder under `subjects/agents/` and `subjects/workflows/`, verifies that the sanitized `name` field inside `subject.json` matches the folder name on disk.
3. **Per-run content hashes** — For each run with a `manifest.json`, recomputes the SHA-256 hash over the run's summary, sorted scenario results, and optional trace, and compares it against the stored `contentHash`.
4. **Compliance evidence audit chain** — For each `evidence.json` under `compliance/`, verifies that `sourceRun.manifestHash` matches the `contentHash` recorded in the source run's `manifest.json`.
5. **Legacy paths** — Runs `LegacyPathScanner` to detect `.AgentEval/` (uppercase), `TestResults/traces/`, and `.agenteval/benchmarks/` and reports them as errors.

After all checks, prints a summary line:

```
Errors: N | Warnings: N | OK: N
```

**Example output (clean workspace)**

```
✔ solution.json OK
✔ Run 3f8a1b2c (subject: TravelAgent)
✔ compliance/OWASP/TravelAgent/2026-04-10T14:32:00Z/evidence.json

Errors: 0 | Warnings: 0 | OK: 3
```

**Example output (issues found)**

```
✔ solution.json OK
✖ Hash mismatch in run 3f8a1b2c (subject: TravelAgent).
✖ Legacy path: TestResults/traces/ - legacy trace artifacts; run `agenteval migrate`

Errors: 2 | Warnings: 0 | OK: 1
```

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | No errors found. |
| `1` | Could not locate a solution root or `.agenteval/` is missing. |
| `2` | One or more validation errors found. |

Warnings (e.g. a subject folder with a missing `subject.json`) do not affect the exit code.

---

### `agenteval migrate`

Migrate legacy AgentEval output paths to the canonical `.agenteval/` layout.

**Synopsis**

```
agenteval migrate [--apply] [--root <path>]
```

**What it does**

Dry-run by default: scans the workspace for legacy paths and prints what would happen. Pass `--apply` to execute the moves.

Three migration paths are handled automatically:

1. **`.AgentEval/` → `.agenteval/`** — Renames the uppercase folder. On Windows (case-insensitive filesystem), uses a two-step move through a temporary name to avoid collisions.
2. **`TestResults/traces/`** — Parses each `{name}_{yyyy-MM-dd}_{suffix}.json` filename, resolves the subject folder, and moves the file to `.agenteval/subjects/agents/{name}/runs/{ts}/traces/`. Files that cannot be parsed or whose subject does not exist are skipped with a warning.
3. **`.agenteval/benchmarks/{Agent}/baselines/`** — Moves each baseline file to `.agenteval/subjects/agents/{Agent}/baselines/v{n}.json` (sequential version numbers per agent). An adjacent `manifest.json` is renamed to `memory-index.json`.

Sample-specific sub-paths (e.g. `.AgentEval/ECS2026MAF_Evals/`) are flagged as requiring manual migration.

**Options**

| Option | Description |
|--------|-------------|
| `--apply` | Execute moves; default is dry-run only. |
| `--root <path>` | Override the auto-detected workspace root path. |

**Example: dry-run**

```
$ dotnet run --project src/AgentEval.Cli -- migrate
[DRY-RUN] Rename .AgentEval → .agenteval (via temp intermediate on Windows).
[DRY-RUN] Move trace: TestResults/traces/TravelAgent_2026-04-10_run1.json
         → .agenteval/subjects/agents/TravelAgent/runs/2026-04-10_run1/traces/TravelAgent_2026-04-10_run1.json
[DRY-RUN] Move baseline: .agenteval/benchmarks/TravelAgent/baselines/baseline.json
         → .agenteval/subjects/agents/TravelAgent/baselines/v1.json
```

**Example: apply**

```
$ dotnet run --project src/AgentEval.Cli -- migrate --apply
[APPLIED] Rename .AgentEval → .agenteval (via temp intermediate on Windows).
  ✔ Renamed /repo/.AgentEval → /repo/.agenteval
[APPLIED] Move trace: TestResults/traces/TravelAgent_2026-04-10_run1.json
         → .agenteval/subjects/agents/TravelAgent/runs/2026-04-10_run1/traces/TravelAgent_2026-04-10_run1.json
  ✔ Moved
```

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Command completed (nothing to do, or moves applied). |
| `1` | Could not locate a solution root. |

---

### `agenteval bench`

Run a benchmark against a subject (agent or workflow). Three benchmark families ship: `gdpr`, `eu-ai-act`, and `agentic`. Each writes results under `.agenteval/compliance/{regulation}/...` (or `.agenteval/benchmarks/agentic/...` for the agentic family) and an audit-chained evidence file the portal and `agenteval doctor` can read.

**Synopsis**

```
agenteval bench gdpr      [--preset <name>] [--subject <name>] [--root <path>] [--input <text>] [--runs <N>]
agenteval bench gdpr      calibrate [--root <path>] [--out <path>]
agenteval bench eu-ai-act [--preset <name>] [--subject <name>] [--root <path>] [--input <text>]
agenteval bench eu-ai-act calibrate [--root <path>] [--out <path>]
agenteval bench agentic   [--preset <name>] [--subject <name>] [--root <path>] [--input <text>] [--budget-tier <tier>]
agenteval bench agentic   calibrate [--root <path>] [--out <path>]
```

**Common options**

| Option | Description |
|--------|-------------|
| `--preset <name>` | Preset selector. GDPR: `smoke` / `standard` / `audit`, plus `+healthcare` / `+hr` / `+childrens` domain packs. EU AI Act: `smoke` / `standard` / `audit`, plus `+high-risk-employment` / `+high-risk-credit` / `+high-risk-education` domain packs. Agentic: `agentic-execution` / `tool-call-accuracy` / `rag-quality` / `judge-quality` / `safety` / `telemetry` / `stochastic-stability` / `conversational` / `reasoning` / `user-experience` / `adversarial-direct`. |
| `--subject <name>` | Subject name (agent or workflow) under evaluation. Default: `default-agent`. |
| `--root <path>` | Workspace root path. Default: auto-detected (walks up to `.sln` / `.slnx` / `.git`). |
| `--input <text>` | Agent input text. Default: built-in fixture. |
| `--budget-tier <tier>` | _Agentic only._ Filter by cost tier: `trivial` / `low` / `medium` / `high` / `all`. Components above the tier are removed and remaining weights renormalised. See [Cost Guidance](benchmarks/agentic/cost-guidance.md). |
| `--runs <N>` | _GDPR only._ Run the benchmark N times and aggregate via `MajorityVote`. Default: 1. |

**Reference docs**

- GDPR: [Getting Started](benchmarks/gdpr/getting-started.md)
- EU AI Act: [Getting Started](benchmarks/eu-ai-act/getting-started.md)
- Agentic: [Getting Started](benchmarks/agentic/getting-started.md) · [Cost Guidance](benchmarks/agentic/cost-guidance.md) · [Evaluator Cards](benchmarks/agentic/evaluator-cards.md)

---

### `agenteval compliance render`

Re-render a PDF report from existing compliance evidence — no LLM cost (the evidence is already on disk).

**Synopsis**

```
agenteval compliance render --regulation <reg> --subject <name> [--ts <timestamp>] [--root <path>]
```

| Option | Description |
|--------|-------------|
| `--regulation <reg>` | Required. Regulation identifier (currently: `gdpr`). |
| `--subject <name>` | Required. Subject name to render evidence for. |
| `--ts <timestamp>` | Timestamp directory (`yyyy-MM-dd_HH-mm-ss`). Defaults to most recent. |
| `--root <path>` | Workspace root. Default: auto-detected. |

---

### `agenteval render`

Re-render a Markdown report from existing benchmark results — no LLM cost.

**Synopsis**

```
agenteval render --benchmark <kind> --subject <name> [--ts <timestamp>] [--root <path>]
```

| Option | Description |
|--------|-------------|
| `--benchmark <kind>` | Required. Benchmark type (currently: `agentic`). |
| `--subject <name>` | Required. Subject name to render results for. |
| `--ts <timestamp>` | Timestamp directory. Defaults to most recent. |
| `--root <path>` | Workspace root. Default: auto-detected. |

---

### `agenteval mc serve`

Start the Mission Control web portal — GraphQL, REST, and SPA on one port — from any working directory. Requires .NET 10. See [Mission Control Getting Started](missioncontrol/getting-started.md).

**Synopsis**

```
agenteval mc serve [--port <N>] [--workspace <path>]
```

| Option | Env var | Default | Description |
|--------|---------|---------|-------------|
| `--port <N>` | `ASPNETCORE_URLS` | `5000` | Bind a different HTTP port. |
| `--workspace <path>` | `AgentEval__Root` | current directory | Workspace root. Mission Control reads `{workspace}/.agenteval/`. |

The CLI spawns `AgentEval.MissionControl(.exe|.dll)` co-located in the same publish directory. The subprocess inherits its working directory from the CLI's bin folder so the SPA's static-asset pipeline resolves correctly; the workspace is plumbed through the `AgentEval__Root` env var.

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Stopped cleanly (Ctrl+C). |
| `1` | Port unavailable, MC assembly missing, or subprocess failed to start. |
| `2` | Running on net8/net9 — Mission Control requires .NET 10. |

---

### `agenteval mc doctor`

Verify Mission Control's runtime artefacts are co-located with the CLI and the SPA bundle is intact. Useful diagnostic before `mc serve` fails with a less-informative error. Sibling to `agenteval doctor` (which validates workspace data, not portal binaries). Requires .NET 10.

**Synopsis**

```
agenteval mc doctor
```

**What it checks**

1. `AgentEval.MissionControl.dll` (and `.exe` on Windows) is present alongside the CLI.
2. `wwwroot/` exists with `index.html` and a populated `assets/` folder (JS + CSS bundles).
3. The Web SDK's static-asset manifest (`*.staticwebassets.endpoints.json` or `*.runtime.json`) is present.
4. On non-Windows, `dotnet` is on PATH (the CLI spawns the MC `.dll` via `dotnet`).

Prints `Errors: N | Warnings: N | OK: N` and exits `2` on any error.

---

## See Also

- [Migration Guide](migration/output-folder.md) — Step-by-step migration from legacy paths.
- [Getting Started](getting-started.md) — C# library quickstart.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical layout, schema versions, audit chain.
- [Mission Control Getting Started](missioncontrol/getting-started.md) — the read-only web portal.
