# AEF: the AgentEval Evidence Format

AEF is an open file format for the evidence an AI-agent evaluation produces: what was run against what, every
result with its typed absences, the gate and release decisions taken on it, and the human decisions added later.
Seals and signatures cover the exact bytes, so anyone can check a result **without trusting the tool that produced
it**, AgentEval included.

> **Status: release candidate.** AEF 1.0 is AgentEval's evidence format
> ([ADR-035](adr/035-aef-is-the-evidence-format.md)). It becomes final at the `aef-1.0` tag, released together with
> AgentEval 1.0. Until then it may still change where implementation or independent review finds a gap. The
> specification lives in the repository at
> [`contracts/aef/`](https://github.com/AgentEvalHQ/AgentEval/tree/main/contracts/aef), under the Apache License 2.0.

## Why a separate format

AgentEval's own output (`.agenteval/`, see [The .agenteval Workspace](agenteval-workspace.md)) is made for
AgentEval. AEF is made for whoever has to rely on a result:

| You want to know | AEF gives you |
|---|---|
| Were the results changed after the run? | A **seal**: SHA-256 digests of every file, in an in-toto statement. Any change is detected. |
| Who produced them? | An optional **signature** (DSSE, ECDSA P-256 or Ed25519) checked against *your* list of trusted keys. |
| Did the agent really pass, or was it never measured? | **Typed absences**: `not_measured`, `not_applicable`, `skipped`, `error` are never counted as a pass or a fail. |
| Was the real agent tested, or a stand-in? | `execution.targetMode` (`live`, `replayed`, `scripted`, `mocked`). Only `live` evidence counts toward a release. |
| May this version ship? | A **checkpoint**: lanes of evidence with rules. A pure decision function recomputes its outcome from the sealed runs. |
| Did a reviewer override a result? | **Overlays**: append-only, sealed batches of human decisions on a closed run. The sealed files never change. |

## What a run looks like

```text
my-run/
  run.json          what was run: subject, suite, judges, how the target was driven, times
  results.ndjson    one line per result: a case's verdict, its checks, trials and rollups
  metrics.json      the metrics the results score
  summary.json      per lane and metric: counts, means, rates, verdicts
  evidence.ndjson   transcripts, tool calls, attachments (in blobs/, by SHA-256)
  gates.ndjson      gate decisions taken during the run (optional)
  seal.json         the seal, once the run is closed
  attestation.dsse.json   its signature (optional)
  overlays/         reviews, overrides and waivers added later, in sealed batches
```

Every file is strict I-JSON, within limits every reader enforces, so two conforming readers never read a file
differently.

## The `agenteval aef` command

| Command | What it does | Exit code 1 when |
|---|---|---|
| `agenteval aef verify <run-dir>` | Checks a run: `intact`, `unsealed` or `invalid`, with every problem. With `--policy`, also who signed it. | the run is invalid |
| `agenteval aef seal <run-dir>` | Seals a closed run. `--key` also signs it (PKCS#8 PEM, ECDSA P-256). `--sealed-by producer\|ingest`. | the run cannot be sealed |
| `agenteval aef view <run-dir>` | Shows the run as its overlays leave it: overrides, reviews, waivers in force at `--at`, withheld blobs. | (never) |
| `agenteval aef checkpoint <file> --runs <dir>` | Checks a checkpoint: its manifest, its runs, each lane's recomputed result, its decision and its signature. | any problem |
| `agenteval aef export <store-dir> <out-dir>` | Converts a run of AgentEval's `.agenteval/` store into an AEF run (an *imported* run, sealed by the exporter). | the converted run does not verify |
| `agenteval aef import assert-ai <dir> <out-dir>` | Converts an [ASSERT](assert-interop.md) run into an AEF run, with ASSERT's harm and over-refusal rates. | the converted run does not verify |

Every command takes `--json` and then prints one JSON value instead of a report. Exit code 2 is a usage or input
error (a missing folder, a trust policy that cannot be read).

For .NET integrations, `AgentEval.Results.AefInputException` identifies an explicit refusal of supplied AEF
data or run state by the writers or job runner. Catch it when reporting invalid input. Producer callback bugs
and `AefWriteException` failures after writing remain errors; do not classify arbitrary `ArgumentException`
or `InvalidOperationException` instances as input refusals. The conformance driver maps recognized input
refusals to exit 2 and unexpected failures to exit 1. The CLI's `aef seal` retains exit 1 for an unsealable run.

### Local dataset runner

`agenteval runner manifest --runner-id local-eval --json` prints the local runner's capabilities.
The ID comes from `--runner-id`, then `AGENTEVAL_RUNNER_ID`, then `runnerId` in `~/.agenteval/runner.json`.
There is no hostname fallback. That optional configuration file supports `runnerId` and a `tags` array only;
the manifest derives platform/runtime/software versions and advertises provider `local` and target mode `live`.
This runner uses process isolation and an endpoint that is already running. Signing identity is omitted until
runner signing is configured and implemented.

Run a plan with `agenteval run --plan ./plans/eval.json --runner-id local-eval --root . --json`.
The root must exist; when omitted it is discovered from the current directory, falling back to that directory.
The job writes `.agenteval/aef/jobs/<jobId>/` with exact `plan.json` bytes, `runner.json`, `events.ndjson`, and
`runs/<runId>/`. Existing job directories are refused. `--json` prints a terminal report containing the job ID,
terminal event, sealed run IDs and any failure reason/limit.

`--aef <dir>` places jobs under `<dir>/jobs/<jobId>/` instead of the workspace's evidence directory.
`--aef-key <pkcs8.pem>` signs every produced run seal, including aborted runs, with an ECDSA P-256 key.
The public `signingKeyId` is recorded in runner options/events and contributes to the job ID; private key material
is never recorded. The library's optional `AefJobOptions.Signer` remains caller-owned.

Runner identity and capture policy belong to the saved plan. `--subject` asserts its exact typed subject reference,
`--subject-version` asserts its version, and `--aef-content on|off` asserts its capture setting. A mismatch is an
input error; these flags do not rewrite the plan. Unlike native standalone commands, runner jobs reject
`--no-aef` and `--aef-unsealed`: the protocol requires sealed evidence. Dry-run validates these controls and
the signing key without requests or file writes. With plan capture on, the dataset runner attaches exact UTF-8
input, expected output and observed response blobs to the case root. Observed judge summaries attach to the root
and criterion explanations to their child results, also through each result's reasoning field. With capture off,
the producer omits these items and the shared writer ignores supplied text without writing blobs. Credentials
and provider error details are excluded in either mode; interrupted cases do not invent missing responses.

After building the CLI, `python tests/AgentEval.Results.Conformance/runner-controls-crosscheck.py net10.0`
(also `net8.0`) checks actual signed identity, explicit output placement, unchanged plan bytes, preflight/dry-run
side effects and agreement between the .NET and Python run/stream/conformance verifiers.

Use `--dry-run --json` to resolve the same plan and credentials and return an acceptance/estimate report without
requests, events or file writes (including diagnostic files). Accepted reports contain `cases`, `calls.target`,
`calls.judge`, `models`, `costRange` (low/high/currency/priceTable), `durationRange` (low/high/unit), `warnings`
and `effectiveMaxUsd`. Call counts are upper bounds, including each suite's readiness and judge retries, and
respect the case limit. Lower cost/time bounds are unknown and reported as zero; the price-table fingerprint
identifies the software price snapshot, not a provider quote or a pricing date. Refusals report `accepted: false`
and a content-free `reason`. Exit codes are 0 for acceptance and 3 for refusal; malformed input remains 2.

`--progress ndjson` sends exactly the persisted `events.ndjson` bytes to stdout, with no terminal summary.
It cannot be combined with `--json` or `--dry-run`. The same terminal exit codes still apply.

`--max-usd <number>` adds a finite, non-negative ceiling; it can only lower the plan's cap.
`--refuse-over-budget` optionally refuses before acceptance when the full upper estimate exceeds that effective
cap. Without this flag the runner admits cases sequentially while their bounds fit, even when the complete plan
estimate is larger. Both dry-run and execution use these same rules.

Additional budget options leave `plan.json` and its digest unchanged. They are recorded in `runner-options.json`
and `job.accepted`/`job.refused`'s `ext."agenteval.runner"`, and contribute to the job ID. Run `costPolicy.maxUsd`
records the lower effective cap. Readers of the base protocol still check the original plan cap; consumers
enforcing the additional operator cap should also inspect the recorded options and run cost policy.

| Outcome | Exit code |
|---|---|
| Job sealed | 0 |
| Unreadable plan or invalid CLI input/configuration | 2 |
| Job refused, cancelled or otherwise failed | 3 |
| Job failed at `maxUsd` | 8 |
| Job stopped at `cases` or `timeout` | 11 |

The plan extension is `ext."agenteval.run"`. The subject endpoint comes from `subject.endpoint`;
`model` names an exact entry in AgentEval's model price table, defaulting to the decoded `model:` subject name.
Unknown model aliases are refused rather than matched to a similar model's price.

| Setting | Default | Meaning |
|---|---|---|
| `model` | `model:` subject name | Priced subject model |
| `azure`, `deployment` | `false`, model name | Azure adapter and deployment; deployment is Azure-only |
| `inputCap` | 32768 | Maximum UTF-8 request bytes including all roles, instructions, schema and framing allowance |
| `outputCap` | 1024 | Maximum requested output tokens |
| `maxCalls` | 1 for subject; 2 for judge | Calls per role per case, including judge correction/fallback calls |
| `caseSeconds` | 60 | Deadline for one case |
| `preparationSeconds` | 30 | Deadline for readiness probes |
| `closeSeconds` | 5 | Reserved time for closing the run |
| `judge` | absent | Endpoint/budget settings for `judges[0]`; supports `endpoint`, `azure`, `deployment`, `inputCap`, `outputCap`, `maxCalls` |

Suites use `dataset:<relative-path>` with forward slashes, resolved from the plan's directory. JSON, JSONL,
YAML, CSV and TSV snapshots require explicit unique case IDs; IDs are never trimmed or replaced. The digest
uses the same LF-normalised bytes as native eval. A supplied plan digest must match; an omitted digest remains
omitted in the run. Case fields use the ordinary harness's input, expected substring and evaluation criteria.
Tool expectations are refused because this adapter cannot enforce them. Criteria require a plan judge.
The first judge uses the existing default harness prompt; custom rubric digests are refused. A requested judge
provider must match `openai-compatible` or `azure`, according to its adapter.

Each role (`subject` or `judge`) accepts at most one `credentialRefs` entry. Its resolved value is passed directly
as that role's API key, without ambient environment fallback or environment mutation. Use distinct reference
names for different keys; the shared runner refuses one name assigned conflicting values across purposes.
Azure requires a reference. An OpenAI-compatible local endpoint may be keyless. Secrets belong only in the
referenced environment variables, never the plan extension.

Readiness makes one bounded request per configured role before the first case of each suite. Estimates and
admission include that cost and its time bound. Cases run sequentially with fresh role budgets and no shared
conversation history. Missing usage, failed requests and interrupted calls retain their reserved cost and leave
token totals unknown. Reported overruns stop the job; evidence retains actual spend, even when it exceeds the
declared budget. Such a provider violation is not reported as a compliant budget run.

To repeat the offline runner integration checks after restoring the solution:

```bash
bash tests/AgentEval.Results.Conformance/runner-crosscheck.sh net10.0
bash tests/AgentEval.Results.Conformance/runner-crosscheck.sh net8.0
```

The script retains successful, refused and interrupted test jobs temporarily, verifies each sealed run with
`agenteval aef verify` and the Python reference verifier, and compares .NET/Python `stream` and `conform` findings.
An intentional provider overrun must report the expected budget violation in both implementations.
It also drives the actual executable against loopback HTTP: dry-run with diagnostic flags, exact progress bytes,
separate subject/judge keys, case/time/cost limits, upfront and unresolved-target refusals, readiness failure,
missing usage, provider overrun and SIGINT cancellation. SIGINT is exercised on POSIX hosts; Windows explicitly
skips that process-signal scenario, while cooperative cancellation remains covered by the .NET tests.
No external model endpoint or paid call is used. Temporary fixtures are removed on exit.

### Export a run, check it, sign it

```bash
# Convert one run of the .agenteval store (store v1 records no target mode: say so if it ran live)
agenteval aef export .agenteval --run 2026-10-08T120000Z-a1b2 ./aef-run --target-mode live --no-seal

# Seal and sign it (the key is yours; keep it out of the run)
agenteval aef seal ./aef-run --key ./signing-key.pem --sealed-by ingest

# Anyone can now check it, with the public key in their own trust policy
agenteval aef verify ./aef-run --policy ./trusted-keys.json
```

A **trust policy** lists the public keys you trust, each with the identity it speaks for:

```json
{"keys": [{"identity": "git:release-bot@example.com", "publicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n"}]}
```

A verifier never trusts a key because a run names it: which keys to trust is always your input.

### What an exported run says, and does not

An exported store run is marked as imported, and lists what the exporter *supplied* rather than read
(`imported.asserted`). Store v1 does not record how the target was driven, so `targetMode` defaults to `mocked`. An
exported run is never passed off as live evidence unless you say it was. The full mapping is in the specification,
§7.5.

## Runs from eval, redteam and bench

`agenteval eval`, `agenteval redteam` and the `agenteval bench` families write each run they make as a sealed AEF
run. Nothing to switch on: in a workspace (a folder with `.agenteval/`, see `agenteval init-workspace`) every run lands
in `.agenteval/aef/runs/<yyyy>/<MM>/<runId>/`, and the command prints its folder and run hash.

| Option | What it does |
|---|---|
| `--aef <dir>` | Write the run under `<dir>/runs/` instead, anywhere. If it cannot be written, the command fails (exit 3). |
| `--no-aef` | Write no AEF run. |
| `--aef-content on\|off` | Persist observed content as linked evidence blobs. Default `off`; incompatible with `--no-aef` when `on`. |
| `--aef-key <pkcs8.pem>` | Sign the native run's seal with an unencrypted ECDSA P-256 private key. The key is loaded before execution. |
| `--aef-unsealed` | Close and verify the native run without sealing it, for a host to seal later. No run hash is reported. Incompatible with `--aef-key`. |
| `--subject <kind:name>` | `eval` and `redteam`: the subject's ref, such as `agent:support-bot` (default `model:<model>`; a built-in redteam target is `agent:<target>`). Bench families use `--subject` as the agent's name. |
| `--subject-version <v>` | The subject's exact version. A checkpoint lane only counts runs that name one. |

Without a workspace and without `--aef`, the command says so and writes no run. Without `--aef`, a run that cannot be
written is a warning, never a failure.

Signing and unsealed controls cannot be combined with `--no-aef`. Use `--aef <dir>` to explicitly require evidence
at a chosen destination. `run --plan` supports signing and explicit placement too, but requires sealed evidence
and checks identity/content flags against the saved plan as described above.

With content capture on, eval records dataset inputs, expected answers, actual responses and observed tool calls;
redteam and scan benchmarks record available probe/control prompts, responses, folded transcripts and grading
explanations; composite benchmarks record available summaries, supporting evidence and recommendations. Observed
judge rationale is also linked through the result's `reasoning` field. Content is stored in blobs referenced by
each result, including individual trials. Synthetic interrupted-trial rollups do not claim a trial's content as
their own. Existing upstream redaction remains in effect; capture does not reconstruct missing content. With
capture off, these fields and blobs are omitted. This flag does not enable request logging or include provider
configuration and signing keys in evidence.

GDPR and EU AI Act attach the reporter's exact rich JSON bytes as a `compliance_artifact` when content capture is
on. OWASP and MITRE attach their content-free control `evidence.json` even with capture off. Each artifact is
cited only by observed roots matching its controls; repeated runs cite aggregate roots, not individual trials.
The producer checks the entire saved document against the report returned by this invocation, including its
source run and manifest hash, before attaching it. Markdown/PDF renderings
are excluded. Reports already produced before a later cancellation can remain attached to the aborted evidence.

After building the CLI, `python tests/AgentEval.Results.Conformance/native-controls-crosscheck.py net10.0`
(also `net8.0`) exercises these controls through the executable against a loopback HTTP server. It checks
preflight/help side effects and verifies signed identity and unsealed evidence with both .NET and Python.

**What each command records**

| Command | Lane | One line per | Notes |
|---|---|---|---|
| `eval` | `eval` | test case (`case`) | Judge criteria at `case/criteria/<n>` and assertions at `case/assertions/<n>`, beside the case's own verdict. With `--runs N`, one line per run and a rollup per case: it passes when at least the number of runs that meets `--success-threshold` passed, fails when it could not have, and is `error` when runs that errored leave it undecided. |
| `redteam` | `redteam` | probe (`probe`, case `<attack>/<probe>`) | Resisted is `passed`; a successful attack is `failed` with `attack.success`, its severity and OWASP LLM / MITRE ATLAS ids; an attack that cannot be measured in the setup is `not_applicable`. Benign controls are lane `over-refusal`, path `control`. |
| `bench gdpr`, `eu-ai-act`, `agentic` | the family | top-level part of the preset (`case`) | The preset's tree below each case: every part's weight, whether it is required and its failure effect, and for every composite the rule that decided it (threshold, severity, a required part that errored, …) and the parts that did. |
| `bench owasp`, `mitre`, `nist` | the family | probe | The scan's probes, as `redteam` writes them; the benchmark's own verdict is in the summary. |
| `bench perf`, `trace-fidelity`, `workflow-trace-fidelity` | the family | part of the result (`case`) | Each part beside its own verdict. |
| `bench longmemeval` | `longmemeval` | question (`case`, its id) | Correct `passed`, wrong `failed`, unscored `not_measured`, unanswered `error`; no question or answer text. |
| `bench typedmemeval` | `typedmemeval` | question (`case`, its id) | `scored`: measured, no pass or fail (the benchmark publishes no pass mark); score 1 when correct; the typed outcome (`abstained`, `missed`, …) in `ext`. |
| `bench autoaudit` | `autoaudit` | one run per model, lines `fidelity` and `safety` | `scored`; the ranking compares the runs and stays in the report. |
| `bench memory` | `memory` | the benchmark (`case`), with one weighted child line per category | `WeightedSum`, as the overall score weighs the categories; each category with the command's label (pass at 70, warn from 50). |

`bench gdpr --runs N` and `bench eu-ai-act --runs N` preserve each trial's tree and a corresponding
majority-vote rollup tree. Ties choose the more severe label; a pass supported by only some requested trials
becomes a warning. Error and skipped trials contribute no vote.

Composite benchmark summaries retain `score@case` as the arithmetic mean. A separate `preset-score@case`
entry records the preset's own aggregate value and verdict, with an `agenteval.*` aggregate method. It is
`not_measured` when the preset withholds its score. These are distinct metrics because AEF allows only one
summary entry per lane, metric and path.

Gates record the command's final decision: eval's golden-trace comparison, redteam's `--fail-on` policy,
and each verdict-bearing benchmark's exit code. TypedMemEval and autoaudit publish scores without a gate.
Redteam and the OWASP, MITRE and NIST benchmarks also record judge token totals when every call reported usage.
Cancellation during eval or redteam execution seals completed results in an `aborted` run with no final gate.
Eval creates its running folder before evaluation and appends each completed case or stochastic trial immediately.
For single-run eval, named `--metrics` results are added as `case/metrics/<name>` children when scoring finishes.
Their verdicts do not change the harness's case verdict or gate; unmeasured metrics carry no score. LLM metric
calls are recorded as judge usage, including when they use the subject's model as the evaluator.
If the process is killed before it can close the run, the folder remains `running` and unsealed with the evidence
already written. An interrupted GDPR, EU AI Act, or agentic benchmark with no completed evaluation has empty results.
Redteam and the OWASP, MITRE, and NIST benchmarks also preserve their completed scan if cancellation occurs
during subsequent persistence or report generation. `--no-aef` still suppresses this evidence.
Live perf benchmarks retain a returned result when persistence fails or reporting is cancelled. Evaluation failures
before a result is returned produce an empty aborted run. Neither path adds a gate; an explicit AEF write failure
returns exit 3, while a default-output failure remains a warning alongside the original failure exit.
Live autoaudit retains each returned model result if a later model or report is interrupted. An active model
without a returned result gets an empty aborted run; later unstarted models get no run. These aborted runs have
no gate. The command rethrows the original cancellation or exception unless explicit evidence output fails (exit 3).
Both trace-fidelity commands also retain their reconciled result when persistence or reporting is interrupted.
Their aborted evidence remains `replayed`, without a command gate; `--no-aef` suppresses it.
The memory benchmark retains completed categories through synchronous progress updates when later work is
cancelled. A partial benchmark has no completed verdict or gate; a returned full result survives persistence
interruption. If no category returned, aborted evidence contains no result rows.
LongMemEval likewise retains completed question results through synchronous progress. Partial runs do not infer
benchmark-wide accuracy, and aborted invocations have no command gate. Existing `RunAsync` callers retain their API;
hosts needing completed-question observations can use `RunWithProgressAsync`.
GDPR, EU AI Act, and agentic benchmarks preserve completed nested evaluations on cancellation too. Unfinished
nodes carry typed absences, and the aborted run has no command gate. Repeated GDPR/EU AI Act runs retain their
started trial trees and original requested trial count. If trial aggregation was interrupted, every observed path
gets an error rollup with exact trial counts; a breakdown absent from another trial is not fabricated. If aggregation
finished before reporting was cancelled, its computed rollups are retained.
An interrupted stochastic case keeps its completed trials and an incomplete rollup.

**How the run says what was tested.** `execution.targetMode` is never assumed:

- `live` only for a target the command itself resolved: `--endpoint`/`--azure`, `--sut <target>` or `--from-env`;
- `replayed` when the command graded an answer or trace the agent already gave (`--response`, `bench agentic`, the
  trace-fidelity families);
- `scripted` for a scripted model (`redteam --sut gatekeeper-demo --scripted`);
- `mocked` for a stand-in. OWASP, MITRE, NIST, GDPR, EU AI Act, agentic, perf and autoaudit `--sut mock` benchmarks write AEF only when `--aef` is explicit;
  they retain exit 11 and an inconclusive command gate. Defaults write nothing, and legacy storage remains off.
  Cancellation preserves an explicitly requested aborted mock run without a gate. Composite mocks retain
  completed children after cancellation or an exception. Their built-in placeholder judge is recorded as code,
  with no LLM model or token usage; supplied evaluators retain their provenance. Autoaudit writes one run per
  scripted subject and retains completed endpoint results if a later endpoint or report is interrupted. An
  interrupted invocation that returned no result is recorded without result lines, including perf's opaque evaluator.

After building the CLI, `python tests/AgentEval.Results.Conformance/native-mock-crosscheck.py net10.0`
(also `net8.0`) checks these eight families through the executable, including defaults, opt-out and write
failures, and verifies the explicit mocked runs with both .NET and Python.

**No content by default.** With the default `contentCapture: off`, runs contain no prompt, response, judge reasoning, criterion
text, assertion name or message, or error message. A typed absence says only its kind ("the probe timed out", "the
judge produced no verdict"). A dataset row without an `id` is written under the loader's id (`item_0`), never its text.
The dataset itself is identified by its digest.

## Checking AEF without AgentEval

AEF does not depend on AgentEval. The specification ships a conformance corpus (over 600 vectors, each expected result
written independently of any implementation) and Python reference tools that use the standard library only:

```bash
python contracts/aef/tools/aef_verify.py run ./aef-run --policy ./trusted-keys.json
python contracts/aef/tools/aef_conformance.py --command "<your implementation>"   # run the corpus against your own
```

AgentEval's .NET implementation (`AgentEval.Results`) is written from the specification text alone and passes the
corpus for every conformance class but Runner. A third implementation, in any language, is welcome: §9 of the
specification says how to claim conformance.

## What AEF does not do

- **A seal is not a signature.** Anyone can edit a file and compute a new seal. Only a signature from a key you trust
  says who stands behind a run.
- **Nothing makes a result true.** AEF makes results attributable and tamper-evident, nothing more.
- **Overlays never change the sealed run.** An override is shown beside the sealed result, never instead of it.

## What AgentEval does with AEF today

- `eval`, `redteam` and the `bench` families write AEF runs directly (above).
- The `agenteval aef` command checks, seals and views runs, checks checkpoints, and converts store v1, ASSERT,
  OpenTelemetry and Inspect runs.

## Read more

- The specification: [`contracts/aef/1/`](https://github.com/AgentEvalHQ/AgentEval/tree/main/contracts/aef/1). Start
  with its primer.
- [ADR-035: AEF 1.0 is AgentEval's evidence format](adr/035-aef-is-the-evidence-format.md)
- [ASSERT Interoperability](assert-interop.md)
