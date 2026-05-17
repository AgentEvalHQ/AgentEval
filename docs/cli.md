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

## Environment variables

The CLI honours the following process-level environment variables.

### `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT`

Real LLM judging requires **all three**. Consumed by:

- `agenteval bench gdpr` · `bench eu-ai-act` · `bench agentic`
- `agenteval bench <regulation> calibrate`

If any of the three are set but others are missing, the command exits **2** with a diagnostic listing the missing variable(s). Partial config is **never** silently downgraded to a stub — the resolver refuses to run rather than produce stub-graded evidence under partial-config conditions.

### `AGENTEVAL_ALLOW_STUB_JUDGE`

Opt-in escape valve for running benchmarks **without** an Azure OpenAI endpoint. Set to `1` or `true` (case-insensitive) to fall back to a deterministic placeholder evaluator that returns score **75/100** and "criterion met" for every criterion.

**Do NOT use in CI.** Stub-mode results are not real judgements; the CLI prints a warning to stderr on every run, and the produced evidence is unsuitable for any compliance claim. Use this only for smoke-testing the pipeline end-to-end without LLM cost.

| Platform | Set the variable |
|---|---|
| Linux / macOS (bash, zsh) | `export AGENTEVAL_ALLOW_STUB_JUDGE=1` |
| Windows (PowerShell) | `$env:AGENTEVAL_ALLOW_STUB_JUDGE = "1"` |
| Windows (cmd) | `set AGENTEVAL_ALLOW_STUB_JUDGE=1` |
| GitHub Actions | `env: AGENTEVAL_ALLOW_STUB_JUDGE: "1"` *(don't — set the AZURE_OPENAI_* secrets instead)* |

**Resolution order** (as of v0.8.1-beta):
1. Test override (programmatic; not user-visible).
2. All three `AZURE_OPENAI_*` set → real Azure OpenAI judge.
3. Any of the three set but not all three → exit 2 with diagnostic.
4. None set + `AGENTEVAL_ALLOW_STUB_JUDGE=1` → stub judge (with stderr warning).
5. None set + no opt-in → exit 2 ("Set AZURE_OPENAI_… or AGENTEVAL_ALLOW_STUB_JUDGE=1").

### `AgentEval__Root`

Workspace-root override for processes that aren't launched from inside the workspace. Read by `agenteval mc serve` (the Mission Control host) and any program using `AgentEvalServiceCollectionExtensions.AddAgentEvalAll()`. Double-underscore is ASP.NET Core's hierarchical-key separator (`AgentEval:Root` in `appsettings.json` → `AgentEval__Root` as an env var).

### `ASPNETCORE_URLS`

Honoured **only** when launching Mission Control directly (`dotnet run --project src/AgentEval.MissionControl`). `agenteval mc serve` forcibly binds to `http://127.0.0.1:<port>` and overrides this variable — there is no built-in auth in Phase 1, so the CLI hard-pins to loopback. To bind a broader interface (e.g. LAN), run the portal binary directly with your own `ASPNETCORE_URLS` and accept the trust trade-off.

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

### `agenteval migrate`

Migrate legacy AgentEval output paths to the canonical `.agenteval/` layout. Dry-run by default; pass `--apply` to commit changes.

**Synopsis**

```
agenteval migrate [--apply] [--root <path>]
```

**What it does**

Walks the workspace looking for three legacy patterns and reports (or moves, with `--apply`) each to its canonical location:

1. **Uppercase `.AgentEval/`** (Windows-collapsed casing) → lowercase `.agenteval/` (preserves audit-chain integrity by moving in-place on the same volume).
2. **`TestResults/traces/*.json`** legacy trace dumps → `subjects/<kind>/<name>/runs/<runId>/traces/agent-trace.json` per discovered subject (file is renamed to the canonical name).
3. **Flat `.agenteval/benchmarks/`** outside the per-subject hierarchy → `subjects/<kind>/<name>/benchmarks/...`.

The dry-run output lists each move as `MOVE <src> → <dest>` so you can preview before committing. `--apply` performs the moves; `--root <path>` lets you target a specific workspace explicitly instead of the auto-detected one.

**Options**

| Option | Description |
|--------|-------------|
| `--apply` | Commit the moves. Without it, the command only prints what it would do. |
| `--root <path>` | Workspace root path. Default: auto-detected. |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Migration plan printed (dry-run) or applied (`--apply`). |
| `1` | Could not locate a workspace root, or an I/O error occurred during a move. |

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
5. **Stray output paths** — Detects accidentally-created folders that shadow the canonical layout (`.AgentEval/` with mixed case on case-sensitive filesystems, stray `TestResults/traces/`, or a flat `.agenteval/benchmarks/` outside the per-subject hierarchy) and reports them as errors so they can be removed or merged.

After all checks, prints a summary line:

```
Errors: N | Warnings: N | OK: N
```

**Example output (clean workspace)**

```
✔ solution.json OK
✔ Run 3f8a1b2c (subject: TravelAgent)
✔ compliance/GDPR/TravelAgent/2026-04-10_14-32-00/evidence.json

Errors: 0 | Warnings: 0 | OK: 3
```

**Example output (issues found)**

```
✔ solution.json OK
✖ Hash mismatch in run 3f8a1b2c (subject: TravelAgent).

Errors: 1 | Warnings: 0 | OK: 1
```

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | No errors found. |
| `1` | Could not locate a solution root or `.agenteval/` is missing. |
| `2` | One or more validation errors found. |

Warnings (e.g. a subject folder with a missing `subject.json`) do not affect the exit code.

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
| `--regulation <reg>` | Required. Regulation identifier: `gdpr` or `eu-ai-act`. |
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
| `--port <N>` | _(none — see note)_ | `5000` | Bind a different HTTP port. `mc serve` forcibly binds to `http://127.0.0.1:<port>` and **ignores** any pre-set `ASPNETCORE_URLS` (see [Environment variables](#environment-variables)). |
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

- [Getting Started](getting-started.md) — C# library quickstart.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical layout, schema versions, audit chain.
- [Mission Control Getting Started](missioncontrol/getting-started.md) — the read-only web portal.
