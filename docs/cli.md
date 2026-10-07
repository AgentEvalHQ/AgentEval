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

### `AI_INFERENCE_PROVIDER` — which provider the CLI talks to

Every `bench` and `calibrate` command reaches its model through this selector. Set it to one of
`azure`, `bitdeer`, `openai`, `foundry` or `openai-compatible`, then set that provider's variables:

| `AI_INFERENCE_PROVIDER` | Provider | Variables it needs |
|---|---|---|
| `azure` | Azure OpenAI | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT` |
| `bitdeer` | Bitdeer AI Model Studio | `BITDEER_API_KEY` (`BITDEER_ENDPOINT`, `BITDEER_MODEL` have defaults) |
| `openai` | OpenAI | `OPENAI_API_KEY` (`OPENAI_BASE_URL`, `OPENAI_MODEL` have defaults) |
| `foundry` | Azure AI Foundry | `FOUNDRY_ENDPOINT` + `FOUNDRY_API_KEY` + `FOUNDRY_MODEL` |
| `openai-compatible` | any OpenAI-compatible host | `OPENAI_COMPATIBLE_ENDPOINT` + `OPENAI_COMPATIBLE_MODEL` (`OPENAI_COMPATIBLE_API_KEY` optional; a keyless local host gets `no-key-needed`) |

**Leaving the selector unset is supported and backward compatible.** The resolver then auto-detects, in
this order: Azure OpenAI, Bitdeer, OpenAI, Foundry, OpenAI-compatible — the first one whose variables are
all present wins. A machine that has only ever set `AZURE_OPENAI_*` therefore behaves exactly as it did
before this variable existed.

**Endpoint policy:** an endpoint must be `https`, or `http` to loopback. Diagnostics name the *variable*
and the reason, never its value, because a configured URL can carry a token in its user-info, query or
fragment and the diagnostic reaches stderr.

*Provider selection was added in v0.41.0-beta. Before that the CLI assumed Azure OpenAI.*

### `AZURE_OPENAI_JUDGE_ENDPOINT`, `AZURE_OPENAI_JUDGE_API_KEY`, `AZURE_OPENAI_JUDGE_DEPLOYMENT`

An optional **judge override**, independent of the selector above. When all three are set they win
outright, so a capable grader can face a cheap subject in a single run. The same endpoint policy applies.

### `AGENTEVAL_ALLOW_STUB_JUDGE` (retired)

Ignored since 0.43. Through 0.42 it let the `bench` commands, and `calibrate`, run with a placeholder judge that
scored 75/100 with every criterion met, so the results and calibration figures measured no judge. There is no
stand-in judge now: with no provider configured, every command that needs a judge exits 3. To try a command
without a model, use `--sut mock` (see [`agenteval bench`](#agenteval-bench)): it runs a built-in stand-in agent
and a placeholder judge, says MOCK, exits 11 and writes nothing to `.agenteval/`.

**Resolution order** (exit codes per [Exit codes](#exit-codes)):

1. Test override (programmatic; not user-visible).
2. All three `AZURE_OPENAI_JUDGE_*` set → that judge, whatever the selector says.
3. A provider resolves with credentials → the real judge for that provider.
4. Any provider variable or the selector is set, but a provider could not be built → exit 3
   (`RuntimeError`), saying the provider is misconfigured.
5. Nothing configured → exit 3, listing each provider and the variables it would need.

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

A command line that does not parse exits `2` before `--log-file` or `--capture-fixture` is opened, so neither file is created or overwritten — see [Exit codes](#exit-codes).

---

## Fixture capture

`--capture-fixture <path>` is a global option, available on every command. It writes one JSON line per LLM
round-trip to `<path>`, for [`agenteval log-file`](#agenteval-log-file) to turn into a test fixture or to
replay against another model. It is separate from `--log-file`, and the two can be passed together:
`--log-file` is a log for reading, while each `--capture-fixture` line is a self-contained record of one
round-trip, including every message the request sent.

```bash
agenteval eval --dataset my-data.jsonl --azure --deployment-name gpt-4o-mini --capture-fixture capture.jsonl
```

Each line has these fields:

| Field | Content |
|---|---|
| `Index`, `Label` | The round-trip's position among those of the same client, and the client's role (`agent`, `judge`, …). |
| `Kind` | `response`; `error` (the call threw); or `abandoned` (a streamed response the caller stopped reading before it ended). |
| `TimestampUtc`, `ElapsedMs` | When the line was written, and how long the round-trip took. |
| `Request` | The instructions, the options (temperature, max output tokens, model id, tool definitions) and every message sent, as role and text. |
| `Response` | Text, tool calls, finish reason and token usage. Only on `response` lines. |
| `Error` | The full exception text, stack trace included. Only on `error` lines. |

**Treat the file like a `--log-file`.** Strings that match known credential formats are masked before each line
is written; everything else — prompts, responses, personal data — is written as it was sent or received. Never
commit or share the file. It is overwritten on each invocation, and an unwritable path prints one warning and the
command runs without capture.

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
JUnit/XML, Markdown, TRX, CSV, or a structured directory. It supports stochastic reruns (`--runs`),
LLM-as-judge, named metrics (`--metrics`), and the `--output-dir` ADR-002 directory export.

**Key options**

| Option | Description |
|--------|-------------|
| `--dataset <path>` | Required. Input dataset file. |
| `--endpoint <url>` / `--azure` / `--deployment-name <name>` | Choose OpenAI-compatible or Azure OpenAI mode. |
| `--model <name>` | Required for non-Azure endpoints. |
| `--api-key <key>` | API key or environment variable fallback. |
| `--sut copilot-studio` | Evaluate a live Microsoft Copilot Studio agent instead of `--endpoint`/`--azure` — bring your own dataset (prompts + judge criteria); requires `--copilotstudio-config`/`--i-understand-live-side-effects`. See [Copilot Studio](copilot-studio.md). |
| `--system-prompt` / `--system-prompt-file` | Set the agent system prompt inline or from file. A `--system-prompt-file` that does not exist is ignored. |
| `--temperature <value>` | Sampling temperature sent with every agent call. Omitted: nothing is sent and the provider's default applies. Given: the value is sent as given, `0` included. `0` narrows sampling; it does not guarantee identical outputs. |
| `--max-tokens <n>` | Maximum output tokens per agent call. Omitted: nothing is sent. |
| `--metrics <list>` | Comma-separated named metrics to score in addition to the normal pass/fail check (e.g. `llm_relevance,code_tool_success`). Each is scored against the same captured response; the agent is not called again. Omitted: no named metric is scored. An unknown name fails before any network call. Accepted names: `llm_relevance`, `llm_faithfulness`, `llm_context_precision`, `llm_context_recall`, `llm_answer_correctness`, `llm_groundedness`, `llm_coherence`, `llm_fluency`, `llm_bias`, `llm_misinformation`, `llm_task_completion`, `code_tool_success`, `code_tool_efficiency`, `code_toxicity`, `code_skill_disclosure_efficiency`. `agenteval list --type metrics` prints these and also names `--metrics` does not accept, each marked `[library only: not available via --metrics]`: `embed_answer_similarity`, `embed_response_context`, `embed_query_context`, `code_tool_selection`, `code_tool_arguments`, `code_recall_at_k`, `code_mrr` and `ConversationCompleteness`. They need input `--metrics` has no source for (expected tools, an embedding generator, a K; `ConversationCompleteness` scores a multi-turn conversation, not a single response), so construct them in code. LLM-based (`llm_*`) names need `--judge`, or on the `--endpoint`/`--azure` path fall back to the agent's own model; code-based (`code_*`) names need neither. Ignored, with a warning, when `--runs` is greater than 1. |
| `--runs <N>` | Runs per test case. Default `1`. Must be at least `1`; greater than `1` is stochastic mode (below), which needs at least 3 runs. |
| `--success-threshold <0..1>` | Stochastic mode only: the share of a test case's runs that must pass for the test case to pass. Default `0.8`. Not used, and not checked, when `--runs` is `1`. |
| `--judge` / `--judge-model` | Separate LLM-as-judge endpoint/model. |
| `--format <fmt>` | Export format. Default `json`. Not written in stochastic mode. |
| `-o, --output <path>` | Output file for single-file formats. Default: stdout. Not written in stochastic mode. |
| `--output-dir <path>` | Structured directory output (`results.jsonl`, `summary.json`, `run.json`). Not written in stochastic mode. |
| `--quiet` | Suppress the header, progress, summary, and the `--metrics` and `--sut` warnings. Errors, and the stochastic-mode export warning below, are still printed. |

**Stochastic mode (`--runs` greater than 1)**

Each test case is run N times. A test case passes when the share of its runs that passed is at least
`--success-threshold`, and the command exits `1` when any test case does not. The stochastic runner needs at
least 3 runs, so `--runs 2` is a usage error (exit `2`), as are `0` and negative values; in this mode a
`--success-threshold` outside 0–1 is a usage error too. These checks run before anything is loaded or called.

A table and a pass/fail line per test case, and a closing summary, are printed to stderr. **No export is
written**: nothing goes to stdout, to `-o`, or to `--output-dir`, because no exporter accepts a stochastic
result — the report the exporters take holds one score per test, and the JUnit, TRX, CSV and Markdown
exporters do not write its metadata, so a stochastic result would read as a single run. When `--format`, `-o` or
`--output-dir` is given, a warning names each one with its value before the first agent call, and a file
already at one of those paths is left unchanged. `--metrics` is ignored in this mode, with a warning.

**With `--sut`**

The target configures its own model, so `--temperature`, `--max-tokens`, `--system-prompt` and
`--system-prompt-file` are not applied. A warning names the ones given (not printed with `--quiet`).

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Every test case passed. |
| `1` | At least one test case failed — in stochastic mode, its pass rate was below `--success-threshold`. |
| `2` | Usage error: a missing `--dataset`, an unknown option, a value that does not parse (such as `--runs abc`), `--runs` below 1, or a `--runs` or `--success-threshold` value stochastic mode does not accept. |
| `3` | Runtime or configuration error — including a missing `--endpoint`/`--azure`/`--model`, a dataset that does not exist or is empty, an unknown `--metrics` name, and an unknown `--format`, which is reported only after the evaluation has run. |

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

### `agenteval compare`

Compare two runs from the `.agenteval/` store, and print score deltas only when the two runs can be shown to
have measured the same thing.

**Synopsis**

```
agenteval compare --baseline <run-dir> --candidate <run-dir> [--strict] [--json]
```

**What it does**

Reads the scenario files of two run directories — the folders under
`.agenteval/subjects/<kind>/<name>/runs/<runId>/` that hold `manifest.json`, `summary.json` and `scenarios/`.
Pointing at the `scenarios/` folder itself also works. Nothing else is read: no manifest, no workspace
discovery, no network. The `eval --output-dir` layout (`results.jsonl`, `summary.json`, `run.json`) is a
different format with no scenario files.

The comparison is **refused** — the reasons are printed and no delta is — when:

- the two runs do not hold the same set of scenario ids, or share none;
- either run recorded no comparability facts for a shared scenario (runs written before those facts existed); or
- for a shared scenario, one of these axes differs between the runs, or only one run recorded it: `evalKey`,
  `evalVersion`, `effectiveBar` (the pass bar applied, compared exactly), `judge` (judged by a model or not),
  `judge.modelId`, `judge.rubricDigest`, and `stimulus` (a hash of what the scenario was asked).

An axis that **neither** run recorded is not counted as a match: it is printed as a blind spot, before the
deltas. `--strict` refuses on it instead. When neither run used a judge, `judge` matches but `judge.modelId` is
such a blind spot, so `--strict` refuses two runs that were both graded without a judge.

When the runs are comparable, the report lists each shared scenario's baseline score, candidate score and delta,
then the mean delta and how many scenarios recovered (failed, then passed) or regressed (passed, then failed).
A delta too small to show at four decimal places is printed in scientific notation rather than as `0.0000`. Two
findings are printed without blocking the comparison: scenarios where a run recorded no usable chance floor, so
the delta cannot be read against chance, and scenarios graded by a judge running the subject's own model.

**Options**

| Option | Description |
|--------|-------------|
| `--baseline <path>` | Required. The baseline run directory. |
| `--candidate <path>` | Required. The candidate run directory. |
| `--strict` | Also refuse when an axis was recorded by neither run. |
| `--json` | Print the comparison as JSON on stdout instead of the report. `deltas` is `null` when the comparison is refused. |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | Comparable; the deltas are printed. |
| `2` | A path is missing or not a directory, holds no scenario files, or holds a file that is not a readable scenario; or a run repeats a scenario id. |
| `13` | Incomparable; the reasons are printed and no delta is. |

---

### `agenteval bench`

Run benchmark families against a subject (agent or workflow). The benchmark registry now includes
GDPR, EU AI Act, Agentic, OWASP, MITRE, NIST, Performance, LongMemEval, TypedMemEval, Memory,
Trace Fidelity, and AutoAudit. Results flow into `.agenteval/` so Mission Control and
`agenteval doctor` can read them (except `autoaudit`, which prints its report and writes it with `--out`).

**Synopsis**

```
agenteval bench --list
agenteval bench <family> [family-specific options]
agenteval bench gdpr calibrate [--root <path>] [--out <path>] [--decisions]
agenteval bench eu-ai-act calibrate [--root <path>] [--out <path>] [--decisions]
agenteval bench agentic calibrate [--root <path>] [--out <path>] [--records <path>] [--limit <n>]
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
| `typedmemeval` | TypedMemEval v5 (AgentEval) — one vertical per run: prospective, episodic, arithmetic, working-memory or forgetting. |
| `memory` | Memory retention / cross-session benchmark. |
| `trace-fidelity` | Chat-boundary vs agent-boundary trace reconciliation. |
| `workflow-trace-fidelity` | Per-executor workflow ledger (tokens + finish reason) vs chat-boundary truth. |
| `autoaudit` | Glass Box auto-audit of the configured models (or `--models a,b`): one support task each, ranked on honesty, safety and cost. Without a provider it refuses; `--sut mock` runs the scripted showcase, labelled MOCK. |

**Notes**

- `agenteval bench --list` prints the registry-backed family catalog.
- **Exit codes:** `bench <family>` and `bench <regulation> calibrate` return **9** (FAIL), **10** (WARN — `bench <family>` only), or **11** (indeterminate) for a benchmark gate outcome, and **3** if the judge fails to configure — see [Exit codes](#exit-codes). A missing required option (`--subject`, and for some families `--input`, `--vertical`, `--agent-trace`/`--chat-trace` or `--workflow-trace`), an `--evidence-detail` value other than `references` or `content`, and `agenteval bench` with no family and no `--list` return **2**.
- Compliance and agentic families support calibration helpers where available. Each calibration report names the judge's provider and model in its header.
- **`bench gdpr calibrate --decisions` and `bench eu-ai-act calibrate --decisions`** grade the golden datasets with the decision model (TypeSafe Jev) instead of the generative judge. The transport is read from `TYPESAFE_API_KEY` or `OPENROUTER_API_KEY` (`JEV_TRANSPORT` forces one, `JEV_MODEL` pins a build); with neither configured the command exits **3**. The report header names the provider (TypeSafe or OpenRouter) and the model that was requested, marked as requested, because the provider may serve a different build under that name. It never contains the key or the endpoint.
- **`typedmemeval` takes `--vertical <prospective|episodic|arithmetic|workingmemory|forgetting>` and `--subject`, both required** — the verticals measure different mechanisms, so there is no default. Corpora are embedded (no download, no dataset path); a real model is required, from whichever provider `AI_INFERENCE_PROVIDER` selects (the `AZURE_OPENAI_*` trio when it is unset), and there is no stub fallback. It prints the typed outcome vector with every denominator and gates on nothing: the family publishes no pass threshold, so a run exits **0** when it measured anything and **11** (`GateIndeterminate`) when it measured nothing, and its run summary is recorded as `WARN` (indeterminate) rather than PASS/FAIL. Cite results as `TypedMemEval-<Vertical> v5 (AgentEval)` — never summed or averaged with LongMemEval numbers. `--vertical prospective` additionally requires the agent under test to implement `ITimestampedHistoryInjectableAgent`; the run refuses before its first provider call otherwise.
- Family-specific options and presets are documented under [Benchmarks](benchmarks.md) and the family pages in the TOC.
- For the Trace Fidelity and AutoAudit families, see the historical design docs under `docs/glassbox-history/` (linked in the TOC under Resources).
- **Every `bench` family that grades an agent needs a real target, or it refuses (exit 2).** `owasp`/`mitre`/`nist`/`perf` take `--from-env` (the configured provider; `--azure-from-env`, its old name, still works), `--sut copilot-studio` (same flags as `eval`/`redteam`) or a generic `--endpoint <url> --model <name> [--api-key <key>]` OpenAI-compatible endpoint. `gdpr`/`eu-ai-act` take `--from-env` or `--sut copilot-studio` (each scenario's prompt is sent to the live agent), or the agent's real answer with `--response`/`--response-file` plus the `--input` it answered. `agentic` grades a supplied `--response`/`--response-file` with its `--input`, plus `--reference`/`--reference-file` (the expected answer) and `--context`/`--context-file` (the retrieved context) for the checks that grade against them (`rag-quality`); without them those checks report not measured. `--sut mock` runs a built-in stand-in instead of an agent: the run says MOCK, exits 11 whatever it scores, and nothing is written to `.agenteval/`. Through 0.42 these commands quietly fell back to a stand-in and stored the result as a measurement.
- **`owasp`/`mitre`/`nist` grade judge first, as `redteam --judge` does.** The judge model comes from the environment (the `AZURE_OPENAI_JUDGE_*` override if set, otherwise the provider `AI_INFERENCE_PROVIDER` selects); there is no option to pick it. With no provider configured the command exits **3**; `--sut mock` needs none and grades with the oracles alone. Before the scan the command makes one short call to the judge and exits **3** if it does not answer. The semantic attacks are graded by Composite Judges (several judge calls per probe); the other attacks by their per-attack oracle, which asks the judge only when it is inconclusive, and the judge may then only raise the probe to "attack succeeded". If a judge call fails during the scan, or the scan runs out of time, the run is INCOMPLETE: stored as `WARN` and exit **11**, never a pass — unless what it did measure already fails it,
which stays a fail (`FAIL`, exit **9**). See the [OWASP](benchmarks/owasp/getting-started.md#presets) and [MITRE](benchmarks/mitre/getting-started.md#presets) pages for probe counts.

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

The metrics catalogue marks every name that `eval --metrics` cannot resolve with
`[library only: not available via --metrics]`. Whether a name carries the marker is read from the same table
`--metrics` resolves against, so the listing cannot offer a name the command then refuses. Marked names need
input `--metrics` has no source for (expected tools, an embedding generator, a K); construct them in code.

**Options**

| Option | Description |
|--------|-------------|
| `--type <metrics|attacks|exporters|datasets>` | Print a single catalogue instead of all four. |

**Exit codes:** `0` when the catalogue is printed; `2` for an unknown `--type`.

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
| `--sut` | Built-in target instead of an endpoint: `gatekeeper-demo` (the Gatekeeper demo: the configured model behind the gate, or a labelled scripted model when no provider is configured) or `copilot-studio` (a live Microsoft Copilot Studio agent). |
| `--scripted` | With `--sut gatekeeper-demo`: run the scripted model even when a provider is configured. Deterministic and free; use it for stable CI baselines. A baseline taken on one model is refused against a run on another (exit 3). See [Attack the gate](gatekeeper/attack-the-gate.md). |
| `--attacks` | Comma-separated attack list; `--pack` imports external benchmark packs. |
| `--transform` | Also run every single-turn probe encoded: codecs (`base64`, `rot13`, `hex`, …) or a group (`reversible`, `lossy`, `all`). The plaintext probes still run; each codec multiplies the probe count. See [Transform pipeline](redteam.md#transform-pipeline). |
| `--judge` / `--attacker` | Separate judge/attacker models for LLM-as-judge and attacker-LLM flows. |
| `--format` / `-o` | Export format and output destination. |
| `--baseline`, `--save-baseline`, `--fail-on` | Regression gating for CI. |
| `--calibration` | Relative scoring against a reference cohort. |
| `--explain` | Attach an LLM rationale to each finding (requires `--judge`). |
| `--benign-controls` | Also run benign look-alike requests and report over-refusal beside the attack success rate, graded by the over-refusal judge (needs `--judge`; does not change the verdict). |

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

`list`, `diff` and `history` inspect the multi-snapshot skill baseline ledger that
`agenteval skills scan --write-baseline` writes to. Each snapshot is a full point-in-time capture (structural
fingerprint + file-content hash per skill, plus that skill's compliance findings at the time) — never
overwritten, so the ledger accumulates history across scans. `approve` works on a different file: the
single-file manifest baseline that Gatekeeper's skill check reads.

**Synopsis**

```
agenteval skills baseline list  [--baseline-root <dir>]
agenteval skills baseline diff  [--baseline-root <dir>] [--since <id>] [--skill <name>] [--hash structural|content]
agenteval skills baseline history <skill-name> [--baseline-root <dir>]
agenteval skills baseline approve <skill-name> --skill-path <dir> --baseline <file> [--note <text>]
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

**`approve <skill-name>`** — re-pins one skill in a manifest baseline file: the `SkillManifestBaseline` JSON that
`skills scan --save-manifest-baseline` writes and `GatekeeperOptions.SkillBaselinePath` points at. Use it after
reviewing a change to that skill, for example when Gatekeeper's skill check has raised a `SkillDriftException`.
It scans `--skill-path` offline (no model call), finds the skill whose manifest name equals `<skill-name>`
exactly (case-sensitive), and replaces that skill's structural fingerprint — and its content hash, when the
skill has a folder on disk. Every other skill's pinned entries are left as they were. The file's capture time
is set to now; `--note` replaces the stored note, and without it the existing note is kept. A missing file or
parent directory is created.

**Options**

| Option | Description |
|--------|-------------|
| `--baseline-root <dir>` (`list`, `diff`, `history`) | Baseline ledger root directory. Default `.agenteval/skills-baselines` (must match what `scan --write-baseline` used). |
| `--since <id>` (`diff` only) | Diff this snapshot's Id against the most recent snapshot, instead of the two most recent. |
| `--skill <name>` (`diff` only) | Only show the diff for this skill. |
| `--hash structural\|content` (`diff` only) | Which hash to diff. Default `content`. |
| `--skill-path <dir>` (`approve` only) | Required. The skill's directory, or a parent directory holding many skills. |
| `--baseline <file>` (`approve` only) | Required. The manifest baseline file to update. |
| `--note <text>` (`approve` only) | A note stored on the baseline — who approved it, and why. |

**Exit codes:** `0` on success (including "nothing to diff yet" — informational, not an error); `3` on a runtime error (e.g. `--since <id>` not found in the ledger, or for `approve`, no skill named `<skill-name>` under `--skill-path`).

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

A missing `--regulation` or `--subject` exits `2`.

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

A missing `--benchmark` or `--subject` exits `2`.

---

### `agenteval log-file`

Work with a file written by [`--capture-fixture`](#fixture-capture).

**Synopsis**

```
agenteval log-file to-fixture <captured.jsonl> --out <fixture.json>
agenteval log-file replay     <captured.jsonl> --out <report.md> (--from-env | --endpoint <url> --model <name> [--api-key <key>]) [--strict-text]
```

**`to-fixture`** writes a JSON array of scripted turns that `ScriptedChatClient.FromFixture` loads, so a test
can play back real model responses with no model behind it. A fixture scripts what the model answered, not what
it was asked, so only responses are kept: each `response` line becomes a turn (text, tool calls, finish reason,
token counts); each `error` line becomes a turn that throws the first line of the captured exception; and
`abandoned` lines are skipped, with a note on stderr saying how many.

**`replay`** reads the capture itself — not a `to-fixture` output, which no longer holds the requests — and
resends the request of each `response` line to another target, one at a time and independently: the same
messages, instructions, temperature, max output tokens, model id and tool definitions. Tools are declared to
the target but never run. Each round-trip is compared on structure, not wording:

| Verdict | When |
|---|---|
| Fail | The target threw; the tool calls differ (tool names and argument names, with the number of calls); the finish reason differs; or, with `--strict-text`, the text differs. |
| Flag | Tool calls and finish reason match, but the response shape differs — a length bucket (under 100, up to 500, or over 500 characters) plus whether the text has a list or a code block. |
| Pass | Tool calls, finish reason and response shape all match. |

Token usage and latency are shown for information and never change a verdict. `error` and `abandoned` lines
are not replayed; the report counts them as skipped. The Markdown report is written to `--out` and also printed
to stdout.

**Options**

| Option | Description |
|--------|-------------|
| `<captured>` | Required, positional. A file written by `--capture-fixture`. |
| `--out <path>` | Required. `to-fixture`: the fixture JSON array. `replay`: the Markdown report. A missing parent directory is created. |
| `--from-env` (`replay`) | Replay against the provider `AI_INFERENCE_PROVIDER` selects (see [Environment variables](#environment-variables)). `--azure-from-env` is its old name and still works. Checked before `--endpoint`. |
| `--endpoint <url>` / `--model <name>` / `--api-key <key>` (`replay`) | Replay against an OpenAI-compatible endpoint. `--model` is required with `--endpoint`. Without `--api-key`, `OPENAI_API_KEY` is used, and without that a placeholder key for keyless local servers. |
| `--strict-text` (`replay`) | Also fail a round-trip whose text is not identical. Off by default: model output is not reproducible, even against the same model with the same settings. |

**Exit codes**

| Code | Meaning |
|------|---------|
| `0` | `to-fixture`: the fixture was written. `replay`: no round-trip failed (flags do not fail the run). |
| `1` | `replay`: at least one round-trip failed. |
| `2` | `replay`: no target was given, `--endpoint` was given without `--model`, or `--from-env` found no configured provider. |
| `3` | The capture file does not exist, or another error occurred (for example, a line that is not a capture record). |

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
| `2` | Usage error (bad flags, malformed input). Reserved strictly for bad-argument paths — see BUG-22 below. Every command returns it for a parse error (see below). `bench`, `compliance render` and `render` also return it for an argument they reject themselves before the run starts: a missing `--subject`; an unknown preset or domain pack (every `bench` family) or vertical (`bench typedmemeval`); an invalid `--budget-tier`; an invalid `--sut` configuration or `--endpoint` without `--model`; a `--response-file` that cannot be read; no target at all (every `bench` family that grades an agent, and `redteam` and `eval`), `--sut mock` together with a real target, or a `--response` without the `--input` it answered. A missing `.agenteval/` workspace is not an argument error: those commands exit `1` and tell you to run `agenteval init-workspace`. |
| `3` | Runtime error (connection/model/IO failure). **Also** returned when a judge fails to build (`JudgeFactory` — missing or partial Azure OpenAI credentials, or a thrown exception constructing the client): that's a runtime/config problem, not a bad CLI argument. `redteam --sut gatekeeper-demo --baseline` also returns it when the baseline was taken on a different model (scripted vs real, or another real model). |
| `4` | Regression vs a supplied `--baseline` — `redteam --fail-on regression` gate (a NEW finding vs pre-existing). |
| `5` | `gatekeeper inspect` — a gate **Blocked** on real evidence. |
| `6` | `gatekeeper inspect` — **fail-closed**: the CLI could not evaluate (e.g. a history gate with no `messages`). Not a policy block. |
| `7` | `gatekeeper inspect` — **not certified**: the honesty guard refused an un-calibrated judge (run `calibrate --certify`, or pass `--allow-uncalibrated`). |
| `8` | `redteam --sut copilot-studio` — a live scan hit its `--max-credits` cap (BudgetExceeded). Enforced as an ESTIMATE (turns counted, not metered spend — the SDK exposes no real credit-cost field); see [Copilot Studio](copilot-studio.md#what---max-credits-does-today). |
| `9` | `bench <family>` / `bench <reg> calibrate` — the composite/calibration gate is a hard **FAIL**. |
| `10` | `bench <family>` — the composite gate is a **WARN** (soft finding, below ideal but not a hard failure). Calibration commands never return this — their thresholds are pass/fail binary. |
| `11` | `bench <family>` — the composite gate could not produce a conclusive verdict (e.g. `skipped`). Every `--sut mock` run exits `11` whatever it scores: it measured no agent. |
| `12` | Reserved by [ADR-031](adr/031-eval-packs-ship-reduced.md). No command returns it. |
| `13` | `compare` — **incomparable**: the two runs could not be shown to have measured the same thing, so no delta was emitted (`Incomparable`). Distinct from `11`, which means a run produced nothing scoreable: here both runs produced verdicts, and comparing them would be meaningless. See [`agenteval compare`](#agenteval-compare). |

**Parse errors exit `2`.** An unknown command or option, a value that does not parse (`--runs abc`), a missing
required option, or no command at all exits `2`. System.CommandLine prints the errors to stderr and the command's
help to stdout, as it always has; only the exit code differs from System.CommandLine's default, which is `1` and
would read as a test failure. `--help` exits `0`, including on an otherwise incomplete command line
(`agenteval eval --help` without `--dataset`), and so does `agenteval --version`.

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
