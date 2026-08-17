# CLI Reference

AgentEval ships a CLI for managing the `.agenteval/` workspace from the terminal and CI/CD pipelines — and, via the
`gatekeeper` verb group, a **language-neutral runtime-policy service** any process (Python, Node, bash, a CI step) can
call for a versioned gate verdict + exit code.

## Installation

```bash
# Recommended — install once, use anywhere
dotnet tool install --global AgentEval.Cli --prerelease

# Update later
dotnet tool update --global AgentEval.Cli --prerelease

# Or run from a cloned repo (contributor / development path)
dotnet run --project src/AgentEval.Cli -- <command>
```

After global install, the `agenteval` command is available system-wide. **Requires .NET 8
SDK or later** for the core surface; **`agenteval mc serve` additionally requires .NET 10**
because Mission Control depends on Hot Chocolate 16 + `MapStaticAssets` (net10-only). On
.NET 8/9 installations, `mc serve` exits with a graceful "requires .NET 10" message rather
than failing obscurely.

Examples below use the global `agenteval` form. To run from a cloned repo, substitute
`dotnet run --project src/AgentEval.Cli --` (note the trailing `--`).

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

**Resolution order** (as of v0.8.1-beta; exit codes updated for BUG-22, see [Exit codes](#exit-codes)):
1. Test override (programmatic; not user-visible).
2. All three `AZURE_OPENAI_*` set → real Azure OpenAI judge.
3. Any of the three set but not all three → exit 3 (`RuntimeError`) with diagnostic.
4. None set + `AGENTEVAL_ALLOW_STUB_JUDGE=1` → stub judge (with stderr warning).
5. None set + no opt-in → exit 3 ("Set AZURE_OPENAI_… or AGENTEVAL_ALLOW_STUB_JUDGE=1").

### `AgentEval__Root`

Workspace-root override for processes that aren't launched from inside the workspace. Read by `agenteval mc serve` (the Mission Control host) and any program using `AgentEvalServiceCollectionExtensions.AddAgentEvalAll()`. Double-underscore is ASP.NET Core's hierarchical-key separator (`AgentEval:Root` in `appsettings.json` → `AgentEval__Root` as an env var).

### `ASPNETCORE_URLS`

Honoured **only** when launching Mission Control directly (`dotnet run --project src/AgentEval.MissionControl`). `agenteval mc serve` forcibly binds to `http://127.0.0.1:<port>` and overrides this variable — there is no built-in auth in Phase 1, so the CLI hard-pins to loopback. To bind a broader interface (e.g. LAN), run the portal binary directly with your own `ASPNETCORE_URLS` and accept the trust trade-off.

---

## Troubleshooting: `--log-file <path>`

A global option — available on **every** command, not just the ones shown below. Writes a human-readable, plain-text log of every LLM round-trip (request + response, including tool calls, usage, and finish reason) to `<path>`, separate from the command's normal stdout/stderr. Also captures the full exception (type + message + stack trace) for any request that fails, not just the short one-line summary the CLI prints to stderr by default.

```bash
agenteval eval --dataset my-data.jsonl --azure --deployment-name gpt-4o-mini --log-file trace.log
agenteval gatekeeper calibrate --gate judge:crescendo-trajectory-turn-shift --azure --deployment-name gpt-4o-mini --log-file calibrate-debug.log
```

Covers every LLM call the CLI makes for the invoked command — the agent/SUT under test, judge, attacker (RedTeam Crescendo/PAIR/TAP), and Copilot Studio's live connector all get logged when active.

**⚠️ Contains raw, unredacted content.** The log file includes the full text of every prompt and response — which can carry secrets, PII, or anything else present in your data or the model's output. `--log-file` is opt-in specifically for troubleshooting: turning it on means you want to see exactly what was sent and received. **Never commit or share the resulting file.** The file is overwritten on each invocation, so a fresh run always starts clean.

An unwritable `--log-file` path (missing parent directory, no permissions) never fails the command — it prints one warning to stderr and the invoked command runs exactly as it would without `--log-file` at all. Verbose logging is a debugging aid; it must never be why an otherwise-successful run fails.

---

## Commands

### `agenteval init`

Initialize a starter evaluation dataset in the current directory.

**Synopsis**

```
agenteval init [--format yaml|json] [-o <path>] [--force]
```

**What it does**

Writes a sample dataset file for the legacy `eval` command surface. The default output is
`agenteval.yaml`; pass `--format json` for a JSON starter, `-o` to choose a different file,
and `--force` to overwrite an existing target.

**Options**

| Option | Description |
|--------|-------------|
| `--format <yaml|json>` | Output format. Default: `yaml`. |
| `-o, --output <path>` | Output file path. Default: `agenteval.{format}`. |
| `--force` | Overwrite an existing file. |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Dataset written successfully. |
| `2` | Invalid format or the target file already exists. |

---

### `agenteval init-workspace`

Initialize the canonical `.agenteval/` workspace for the current solution.

**Synopsis**

```
agenteval init-workspace [--name <display-name>]
```

**What it does**

Walks up from the current directory until it finds a `.sln`, `.slnx`, or `.git` marker and treats
that directory as the workspace root. Creates `.agenteval/` if it does not exist, then writes:

- `solution.json` — solution-level identity: a random UUID, the display name, and `schemaVersion: "1.0"`.
- `README.md` — overview of the workspace layout.
- `.gitignore` — excludes per-run artifacts and red-team outputs from source control.

If `solution.json` already exists, the command reports that the workspace is already initialized and
exits cleanly.

**Options**

| Option | Description |
|--------|-------------|
| `--name <display-name>` | Display name to record in `solution.json`. Defaults to the directory name of the solution root. |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Initialized successfully (or already initialized). |
| `1` | Could not locate a solution root. |

---

### `agenteval eval`

Evaluate an AI agent against a dataset.

**Synopsis**

```
agenteval eval --dataset <path> --endpoint <url> [--model <name>] [--azure --deployment-name <name>] [options]
```

**What it does**

Loads a YAML, JSON, JSONL, CSV, or TSV dataset, evaluates the agent, and exports results as JSON,
JUnit/XML, Markdown, TRX, CSV, or a structured directory. It supports stochastic reruns,
LLM-as-judge, custom metrics, and the `--output-dir` ADR-002 directory export.

**Key options**

| Option | Description |
|--------|-------------|
| `--dataset <path>` | Required. Input dataset file. |
| `--endpoint <url>` / `--azure` / `--deployment-name <name>` | Choose OpenAI-compatible or Azure OpenAI mode. |
| `--model <name>` | Required for non-Azure endpoints. |
| `--api-key <key>` | API key or environment variable fallback. |
| `--sut copilot-studio` | Evaluate a live Microsoft Copilot Studio agent instead of `--endpoint`/`--azure` — bring your own dataset (prompts + judge criteria); requires `--copilotstudio-config`/`--i-understand-live-side-effects`. See [Copilot Studio](copilot-studio.md). |
| `--system-prompt` / `--system-prompt-file` | Set the agent system prompt inline or from file. |
| `--temperature` / `--max-tokens` | Sampling and output-length controls. |
| `--metrics <list>` | Comma-separated named metrics to score ADDITIONALLY, alongside the normal pass/fail gate (e.g. `llm_relevance,code_tool_success`) — each is scored against the SAME captured response, never a second agent call. An unknown name fails fast, before any network call. **Resolvable names today** (v1, not the same list `agenteval list --type metrics` prints — that list is broader/aspirational, see the note below): `llm_relevance`, `llm_faithfulness`, `llm_context_precision`, `llm_context_recall`, `llm_answer_correctness`, `llm_groundedness`, `llm_coherence`, `llm_fluency`, `llm_bias`, `llm_misinformation`, `llm_task_completion`, `code_tool_success`, `code_tool_efficiency`, `code_toxicity`, `code_skill_disclosure_efficiency`. LLM-based (`llm_*`) names need `--judge` (or fall back to the SUT's own model on the `--endpoint`/`--azure` path); code-based (`code_*`) names need neither. Not yet wired for `--runs > 1` (stochastic mode warns and ignores it), and a handful of names `agenteval list --type metrics` shows are not yet resolvable via `--metrics` at all — `code_tool_selection`/`code_tool_arguments` (need per-test-case config `--metrics` has no source for), `code_mrr`/`code_recall_at_k`/`embed_*` (parametrized or embedding-only), and `ConversationCompleteness` (a different evaluation shape, not the standard metric interface). |
| `--runs <N>` / `--success-threshold <N>` | Stochastic evaluation controls. |
| `--judge` / `--judge-model` | Separate LLM-as-judge endpoint/model. |
| `--format <fmt>` | Export format. |
| `-o, --output <path>` | Output file for single-file formats. |
| `--output-dir <path>` | Structured directory output (`results.jsonl`, `summary.json`, `run.json`). |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Evaluation completed successfully. |
| `1` | Test failure or validation error. |
| `3` | Runtime error. |

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

Run benchmark families against a subject (agent or workflow). The benchmark registry now includes
GDPR, EU AI Act, Agentic, OWASP, MITRE, NIST, Performance, LongMemEval, TypedMemEval, Memory,
Trace Fidelity, and AutoAudit. Results flow into `.agenteval/` so Mission Control and
`agenteval doctor` can read them.

**Synopsis**

```
agenteval bench --list
agenteval bench <family> [family-specific options]
agenteval bench gdpr calibrate [--root <path>] [--out <path>]
agenteval bench eu-ai-act calibrate [--root <path>] [--out <path>]
agenteval bench agentic calibrate [--root <path>] [--out <path>]
```

**Families**

| Family | Purpose |
|--------|---------|
| `gdpr` | GDPR compliance benchmark. |
| `eu-ai-act` | EU AI Act compliance benchmark. |
| `agentic` | Agentic tool-use benchmark family. |
| `owasp` | OWASP LLM Top 10 red-team benchmark. |
| `mitre` | MITRE ATLAS red-team benchmark. |
| `nist` | NIST AI RMF-style red-team benchmark. |
| `perf` | Latency / throughput / cost benchmark. |
| `longmemeval` | Long-context memory benchmark. |
| `typedmemeval` | TypedMemEval v4 (AgentEval) — one vertical per run: prospective, episodic, arithmetic, working-memory or forgetting. |
| `memory` | Memory retention / cross-session benchmark. |
| `trace-fidelity` | Chat-boundary vs agent-boundary trace reconciliation. |
| `workflow-trace-fidelity` | Per-executor workflow ledger (tokens + finish reason) vs chat-boundary truth. |
| `autoaudit` | GlassBox-style multi-endpoint workflow auto-audit. |

**Notes**

- `agenteval bench --list` prints the registry-backed family catalog.
- **Exit codes:** `bench <family>` and `bench <regulation> calibrate` return **9** (FAIL), **10** (WARN — `bench <family>` only), or **11** (indeterminate) for a benchmark gate outcome, and **3** if the judge fails to configure — see [Exit codes](#exit-codes).
- Compliance and agentic families support calibration helpers where available.
- **`typedmemeval` takes `--vertical <prospective|episodic|arithmetic|workingmemory|forgetting>` and `--subject`, both required** — the verticals measure different mechanisms, so there is no default. Corpora are embedded (no download, no dataset path); `AZURE_OPENAI_*` is required and there is no stub fallback. It prints the typed outcome vector with every denominator and gates on nothing: the family publishes no pass threshold, so a run exits **0** when it measured anything and **11** (`GateIndeterminate`) when it measured nothing, and its run summary is recorded as `WARN` (indeterminate) rather than PASS/FAIL. Cite results as `TypedMemEval-<Vertical> v4 (AgentEval)` — never summed or averaged with LongMemEval numbers. `--vertical prospective` additionally requires the agent under test to implement `ITimestampedHistoryInjectableAgent`; the run refuses before its first provider call otherwise.
- Family-specific options and presets are documented under [Benchmarks](benchmarks.md) and the family pages in the TOC.
- For the Trace Fidelity and AutoAudit families, see the historical design docs under `docs/glassbox-history/` (linked in the TOC under Resources).
- **`owasp`/`mitre`/`nist` reach a live target** beyond the default built-in stub / `--azure-from-env`: `--sut copilot-studio` (same flags as `eval`/`redteam`) or a generic `--endpoint <url> --model <name> [--api-key <key>]` OpenAI-compatible endpoint. **`gdpr`/`eu-ai-act` also support `--sut copilot-studio`** (drives the live agent per-scenario instead of grading a static `--response`) — no generic `--endpoint` for these two yet. `agentic`/`memory`/`perf` do not have `--sut` at all — whether they ever should is an open product question, not just unbuilt.

---

### `agenteval list`

List the legacy command-surface catalogues used by `eval` / `redteam`.

**Synopsis**

```
agenteval list [--type metrics|attacks|exporters|datasets]
```

**What it does**

Prints the available metrics, attack types, export formats, and dataset formats. With no filter it
prints all four catalogues.

**Options**

| Option | Description |
|--------|-------------|
| `--type <metrics|attacks|exporters|datasets>` | Print a single catalogue instead of all four. |

---

### `agenteval redteam`

Run low-level red-team scans against an agent. This is the fully parameterised scanner surface; the
`bench owasp` and `bench mitre` families wrap curated presets around it.

**Synopsis**

```
agenteval redteam [--azure] [--endpoint <url>] [--model <name>] [--deployment-name <name>] [--attacks <list>] [--format <fmt>] ...
```

**Key options**

| Option | Description |
|--------|-------------|
| `--azure` / `--endpoint` / `--deployment-name` | Azure OpenAI mode. |
| `--endpoint` / `--model` | OpenAI-compatible mode (OpenAI, Ollama, Groq, vLLM, LM Studio, etc.). |
| `--sut` | Built-in target instead of an endpoint: `gatekeeper-demo` (credential-free demo) or `copilot-studio` (a live Microsoft Copilot Studio agent). |
| `--attacks` | Comma-separated attack list; `--pack` imports external benchmark packs. |
| `--judge` / `--attacker` | Separate judge/attacker models for LLM-as-judge and attacker-LLM flows. |
| `--format` / `-o` | Export format and output destination. |
| `--baseline`, `--save-baseline`, `--fail-on` | Regression gating for CI. |
| `--calibration` | Relative scoring against a reference cohort. |
| `--explain` | Attach an LLM rationale to each finding (requires `--judge`). |

For the full flag matrix and examples, see [Red Team Security](redteam.md). Exit codes: see [Exit codes](#exit-codes).

---

### `agenteval gatekeeper`

Run Gatekeeper runtime-enforcement gates from the terminal or any language — the same policy you red-team with,
exposed as a **language-neutral policy service**. A process pipes a JSON payload to `gatekeeper inspect` and gets a
versioned verdict JSON + an exit code; the deterministic gates need **no credentials** and are byte-stable, so they
drop straight into CI.

**Synopsis**

```
agenteval gatekeeper list-gates [--json] [--phase inspect|serve|all]
agenteval gatekeeper inspect   --gate <id> [--input <file.jsonl>] [--policy block|warn] [gate flags] [model flags]
agenteval gatekeeper calibrate --gate judge:<axis> <model flags> [--certify]
agenteval gatekeeper serve                                # stub — not implemented
```

**Subcommands**

| Subcommand | Purpose |
|---|---|
| `list-gates` | List every gate, its state class, whether it needs a model, and its span policy. |
| `inspect` | Evaluate one JSON payload (stdin) or one per line (`--input` JSONL) against a gate/panel; emits a versioned verdict + exit code. |
| `calibrate` | Score a `judge:<axis>` against its gold set + keyword-oracle baseline; `--certify` writes the certificate the honesty guard reads. |
| `serve` | Reserved for stateful accumulator gates — **not implemented**. |

`judge:*` gates read/write a per-model calibration certificate under `.agenteval/gatekeeper/certs/` (override with
`--cert-dir`); the deterministic and tool gates are credential-free and CI-safe. For the full flag matrix, the verdict
JSON contract, and the honesty guard, see [Gatekeeper from any language](gatekeeper-cli.md).

---

### `agenteval skills scan`

Static, offline compliance scan of a directory of MAF Agent Skills — no model call, no credentials. Reaches the
same `SkillComplianceValidator`/`MafSkillScanner` library code the [Agent Skills](agent-skills.md) evaluation
suite ships, from the CLI.

**Synopsis**

```
agenteval skills scan <path> [--format console|markdown|json] [-o|--output <file>] [--fail-on-noncompliant]
                              [--write-baseline] [--baseline-root <dir>] [--repo] [--check-baseline]
                              [--save-manifest-baseline <file>] [--manifest-baseline <file>] [--baseline-note <text>]
```

**What it does**

Walks `<path>` for `SKILL.md`-rooted skill folders (the same convention MAF's own `AgentFileSkillsSource`
discovers by), checks each against the GA `SKILL.md` authoring rules (name/description/compatibility) plus
governance flags (script-execution review, untrusted resource sources, experimental `allowed-tools`), and
renders a report. v1 is compliance-only — the composite Skill Health & Security Index is library-only for now
(see [Agent Skills](agent-skills.md)). Three additional governance signals ride along in the same report:
cross-location content drift (`--repo`/`scan-workspace` only, always on), trust-on-first-use reputation
matching (`--check-baseline`, opt-in), and manifest hash-pin drift against an explicit trust-time pin
(`--manifest-baseline`, opt-in) — see [Agent Skills](agent-skills.md#2--compliance-scanner) for how each works.

**Options**

| Option | Description |
|--------|-------------|
| `<path>` | Required, positional. Directory containing one or more skill folders (a REPO ROOT when `--repo` is set). |
| `--format <fmt>` | `console` (default), `markdown`, or `json`. |
| `-o, --output <path>` | Write the rendered report to a file instead of stdout. |
| `--fail-on-noncompliant` | Exit `1` when the scan finds a High-severity finding. Default off (informational-only). Cross-location drift (Medium) and previously-vetted matches (Low) never trigger this; a manifest-baseline drift finding (High) DOES. |
| `--write-baseline` | Capture a timestamped baseline snapshot (structural fingerprint + full file-content hash per skill) into the baseline ledger. See [`agenteval skills baseline`](#agenteval-skills-baseline) below. |
| `--baseline-root <dir>` | Baseline ledger root directory. Default `.agenteval/skills-baselines`. Used by both `--write-baseline` and `--check-baseline`. |
| `--repo` | Treat `<path>` as a repo root: scan every known skill-directory convention found under it (`.claude/skills`, `.agents/skills`, `.windsurf/skills`, ...) and aggregate the results into ONE combined report, instead of treating `<path>` itself as one skill directory. Per-convention breakdown (found/not-present, skill/finding counts) prints to stderr. When two or more conventions define the same skill name with DIFFERENT content, a `CrossLocationContentDrift` finding is added automatically. |
| `--check-baseline` | Trust-on-first-use: compare each scanned skill's content hash against the baseline ledger's history. A match against any prior snapshot for the same name adds an informational `MatchesPreviouslyVettedCopy` finding. Meaningless on a first-ever scan (no history yet) — pair with `--write-baseline` on earlier runs. |
| `--save-manifest-baseline <file>` | Capture a trust-time hash-pin of every scanned skill's manifest content (name, description, resource/script inventory, allowed-tools, compatibility) to this JSON file. A SINGLE pinned file, distinct from `--write-baseline`'s multi-snapshot ledger — mirrors the RedTeam baseline/diff CI pattern: commit this file, then re-check future scans against it. |
| `--manifest-baseline <file>` | Check every scanned skill against a `--save-manifest-baseline` file. A skill whose manifest content changed since the pin was captured is reported as a High-severity `ManifestChangedSinceBaseline` finding — a possible rug-pull. |
| `--baseline-note <text>` | Optional human note saved alongside `--save-manifest-baseline` (e.g. who approved it, why). |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Scan completed (compliant, or `--fail-on-noncompliant` not set). |
| `1` | `--fail-on-noncompliant` was set and a High-severity finding was found. |
| `3` | Runtime error (e.g. `<path>` does not exist). |

---

### `agenteval skills scan-workspace`

Agent Skills Wave 3a — filesystem-only, credential-free scan across a folder of **already-cloned** repos.
Every immediate (non-hidden) subdirectory of `<path>` is treated as one repo and scanned with the same
pipeline `scan --repo` uses, then combined into one report. No GitHub/GitLab API, no token — the operator's own
clone step is what already decides which repos are visible; that access-control question is deliberately kept
outside this verb's trust boundary (contrast with the still-gated, API-driven `scan-org`, Wave 3b).

**Synopsis**

```
agenteval skills scan-workspace <path> [--format console|markdown|json] [-o|--output <file>] [--fail-on-noncompliant]
                                        [--write-baseline] [--baseline-root <dir>] [--check-baseline]
                                        [--save-manifest-baseline <file>] [--manifest-baseline <file>] [--baseline-note <text>]
```

**What it does**

Fans out `scan --repo`'s existing per-repo pipeline over every immediate subdirectory of `<path>`, combining
findings/coverage into one report (per-repo skill/finding counts print to stderr). Every entry's location is
tagged `{repoFolder}/{conventionPath}`, so cross-location content drift — the same detector `scan --repo`
already uses, unchanged — now also fires **across repos**, not just across conventions within one repo: the
same skill name with different content in two different cloned repos is exactly the drift/poisoning signal
this is for.

```bash
gh repo clone myorg/service-a ~/audit/service-a
gh repo clone myorg/service-b ~/audit/service-b
agenteval skills scan-workspace ~/audit --write-baseline --format json -o report.json
```

**Options**

| Option | Description |
|--------|-------------|
| `<path>` | Required, positional. A folder whose immediate subdirectories are repo roots. |
| `--format <fmt>` | `console` (default), `markdown`, or `json`. |
| `-o, --output <path>` | Write the rendered report to a file instead of stdout. |
| `--fail-on-noncompliant` | Exit `1` when the scan finds a High-severity finding. Cross-location drift (Medium) and previously-vetted matches (Low) never trigger this — only High-severity findings do. |
| `--write-baseline` | Capture a timestamped baseline snapshot across all scanned repos into the baseline ledger. |
| `--baseline-root <dir>` | Baseline ledger root directory. Default `.agenteval/skills-baselines-workspace` — deliberately DIFFERENT from `scan`'s `.agenteval/skills-baselines` default, so a plain `scan --write-baseline` can't accidentally get diffed against a much larger workspace-scale snapshot. Pass the same root to both verbs explicitly if you want one shared ledger. |
| `--check-baseline` | Trust-on-first-use across the whole workspace — see `scan`'s `--check-baseline` above. |
| `--save-manifest-baseline <file>` / `--manifest-baseline <file>` / `--baseline-note <text>` | Same single-pin manifest hash-drift gate as `scan` — see `scan`'s own rows above. A skill name duplicated across repos is deduplicated the same way `--repo` already tolerates it. |

**Known limitation:** `skills baseline diff`/`history` track only one location per skill name even when a
single snapshot legitimately has several (a pre-existing Wave 2 shortcut) — at workspace scale, where the
same name across many repos is the expected case, this means the persisted ledger's diff/history can miss
drift in every repo except whichever sorts first. The live scan-time `CrossLocationContentDrift` finding does
NOT have this limitation. See [Agent Skills](agent-skills.md#2--compliance-scanner) for the full explanation.

**Exit codes:** same as `scan` above.

---

### `agenteval skills baseline`

Inspects the multi-snapshot skill baseline ledger `agenteval skills scan --write-baseline` writes to. Each
snapshot is a full point-in-time capture (structural fingerprint + file-content hash per skill, plus that
skill's compliance findings at the time) — never overwritten, so the ledger accumulates history across scans.

**Synopsis**

```
agenteval skills baseline list  [--baseline-root <dir>]
agenteval skills baseline diff  [--baseline-root <dir>] [--since <id>] [--skill <name>] [--hash structural|content]
agenteval skills baseline history <skill-name> [--baseline-root <dir>]
```

**`list`** — every captured snapshot (Id, capture time, scanned root, skill count), oldest listed first.

**`diff`** — compares two snapshots (default: the two most recent; pass `--since <id>` to diff a specific
snapshot against the latest) using the same `ManifestDriftDetector` primitive `PromptTemplateDriftGate`/
`McpToolDescriptionPoisoningGate` use, over either the structural fingerprint or the full content hash
(`--hash`, default `content` — the stronger signal). A `Changed` skill is flagged `CHANGED + NEW HIGH FINDING`
when the change also introduced a new High-severity compliance finding (vs. `changed, no new High finding`
for a cosmetic-only edit) — this is the "don't cry wolf on every cosmetic edit" guard.

**`history <skill-name>`** — walks the ledger chronologically and reports every point where that skill's
content hash changed, with any High-severity findings present at each change point.

**Options common to all three**

| Option | Description |
|--------|-------------|
| `--baseline-root <dir>` | Baseline ledger root directory. Default `.agenteval/skills-baselines` (must match what `scan --write-baseline` used). |
| `--since <id>` (`diff` only) | Diff this snapshot's Id against the most recent snapshot, instead of the two most recent. |
| `--skill <name>` (`diff` only) | Only show the diff for this skill. |
| `--hash structural\|content` (`diff` only) | Which hash to diff. Default `content`. |

**Exit codes:** `0` on success (including "nothing to diff yet" — informational, not an error); `3` on a runtime error (e.g. `--since <id>` not found in the ledger).

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

## Exit codes

The CLI's exit-code contract, so CI can branch on the outcome. Source of truth: `src/AgentEval.Cli/ExitCodes.cs`.

| Code | Meaning |
|---|---|
| `0` | Success — passed / allowed / no gate blocked. |
| `1` | Test failure — one or more evaluations failed (`eval`, `redteam`). |
| `2` | Usage error (bad flags, malformed input). Reserved strictly for bad-argument paths — see BUG-22 below. |
| `3` | Runtime error (connection/model/IO failure). **Also** returned when a judge fails to build (`JudgeFactory` — missing or partial Azure OpenAI credentials, or a thrown exception constructing the client): that's a runtime/config problem, not a bad CLI argument. |
| `4` | Regression vs a supplied `--baseline` — `redteam --fail-on regression` gate (a NEW finding vs pre-existing). |
| `5` | `gatekeeper inspect` — a gate **Blocked** on real evidence. |
| `6` | `gatekeeper inspect` — **fail-closed**: the CLI could not evaluate (e.g. a history gate with no `messages`). Not a policy block. |
| `7` | `gatekeeper inspect` — **not certified**: the honesty guard refused an un-calibrated judge (run `calibrate --certify`, or pass `--allow-uncalibrated`). |
| `8` | `redteam --sut copilot-studio` — a live scan hit its `--max-credits` cap (BudgetExceeded). Enforced as an ESTIMATE (turns counted, not metered spend — the SDK exposes no real credit-cost field); see [Copilot Studio](copilot-studio.md#what---max-credits-does-today). |
| `9` | `bench <family>` / `bench <reg> calibrate` — the composite/calibration gate is a hard **FAIL**. |
| `10` | `bench <family>` — the composite gate is a **WARN** (soft finding, below ideal but not a hard failure). Calibration commands never return this — their thresholds are pass/fail binary. |
| `11` | `bench <family>` — the composite gate could not produce a conclusive verdict (e.g. `skipped`). |

`redteam` uses `1` for failure, `3` for runtime error, and `4` for a `--fail-on regression` gate. Code `8` is
returned by a live `--sut copilot-studio` scan that hits `--max-credits` (BudgetExceeded) — an estimate, not a
metered value. `gatekeeper`'s `5/6/7` are deliberately distinct exit codes — see
[Gatekeeper from any language](gatekeeper-cli.md#exit-codes).

**BUG-22 (resolved 2026-07-19):** code `2` used to be overloaded — `bench`/`calibrate` returned it for gate
FAIL/WARN as well as bad arguments, and `JudgeFactory` config failures also returned it, so CI could not tell
"invoked wrong" from "agent failed the gate" from "judge misconfigured". This is now split across `2` (bad
arguments only), `3` (judge/runtime config problems), and `9`/`10`/`11` (gate outcomes) as documented above.
**This is a breaking change** for any external CI pipeline that branched on exit code `2` from `bench`/
`calibrate` commands — update those pipelines to check the new codes. See `src/AgentEval.Cli/ExitCodes.cs`
and CHANGELOG.md.

---

## See Also

- [Getting Started](getting-started.md) — C# library quickstart.
- [The `.agenteval/` Workspace](agenteval-workspace.md) — canonical layout, schema versions, audit chain.
- [Gatekeeper from any language](gatekeeper-cli.md) — the `gatekeeper` verb group, verdict JSON, and honesty guard.
- [Gatekeeper (Runtime Enforcement)](gatekeeper/introduction.md) — the runtime enforcement middleware overview.
- [Mission Control Getting Started](missioncontrol/getting-started.md) — the read-only web portal.
