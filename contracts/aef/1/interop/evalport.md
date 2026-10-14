# EvalPort

**Version read:** `adhabnr-ux/evalport` main at `d0e90c48cfa8cabceefb066dcfa757ea3f5c85f3` (2026-10-07): `SPEC.md`
(version **1.0.0-rc.5**, a release candidate), `schema/resultset.json`, `schema/suite.json`, `schema/testcase.json`,
`schema/grader.json`, and `docs/rfcs/outcome-model.md`. The tags `v1.3.x` are versions of the SDK packages. Also read:
the AgentEval adapter at tag `evalport-agenteval-dotnet/v0.1.1-beta` (`1d6e6312`), built against AgentEval
0.43.0-beta. That adapter converts AgentEval's in-memory result trees. It does not read AEF.

## The target

EvalPort defines four JSON documents (JSON Schema 2020-12): **TestCase**, **Grader**, **EvalSuite**, **ResultSet**.
Metadata keys that start with `openeval.` are reserved. The project was called OpenEval before.

- A **ResultSet** requires `version` (the EvalPort version, full semver), `suite_id`, `run_id`, `started_at` and
  `results` (at least one). It may have `suite_version`, `completed_at`, `provider` (`model`, `api_base`,
  `temperature`, `max_tokens`, `extra`), `runner` (`name`, `version`), `isolation` (`fresh`, `shared` or another
  string), `group` (`group_id`, `parent_group_id`, `role`, `label`, `sequence`), `summary` and `metadata`.
- A **Result** requires `test_case_id`, `grader_results` and `passed`. It may have:
  - `actual_output`, `duration_ms`, `completed_at`, `metadata`;
  - `attempt`, 1-based and unique with `test_case_id` and `run_id`;
  - `error` (`type` `timeout`, `provider_error` or `runner_error`; `message`; `code`; `retryable`).
- A **GraderResult** requires `grader_id`, `type`, `score` and `passed`. `score` is a number in [0, 1] or `null`. It
  may have `reason` and `metadata`.

Rules that matter here:

- **Rule 5.** A score must be scaled into [0, 1]. The original value goes in `metadata.openeval.raw_score`.
- **Rule 6.** "Skipped or not-yet-executed graders … MUST be represented with `score: null` and `passed: false`." A
  null score means "not verified", which is different from a scored failure.
- **Aggregation extension.** `metadata.openeval.aggregation` declares how `passed` follows from the graders: `all`
  (the default), `any`, `majority` or `weighted` with a `threshold`. Null scores are left out. A result whose graders
  are all null is `passed: false`, with `metadata.openeval.aggregation_status: "unscored"`.
- **Reserved keys** (Appendix B): `openeval.partial`, `openeval.cost`, `openeval.trace_id`, `openeval.raw_score`,
  `openeval.judge_hardening`, and others.
- **Signing** is a convention outside the schemas. A detached Sigstore bundle `<file>.sigstore.json` signs the
  published bytes, with no canonical form, and the verifier must check the signer's identity.

**Open proposal.** Discussion #49 proposes an optional `Result.verdict`: `passed`, `failed` or `unverified`. It is not
in the schema, which rejects it. An alternative, PR #128, keeps the schema as it is and requires an aggregation
declaration on partly scored results. Both are drafts. The adapter can emit `verdict` as an opt-in.

## AEF → EvalPort

One run gives one ResultSet. Each root line (no `parentResultId`) gives one Result. Each leaf under it gives one
GraderResult.

| AEF | EvalPort | Fidelity |
|---|---|---|
| `runId` | `run_id` | exact |
| `suite.ref`, `suite.version` | `suite_id`, `suite_version` | exact |
| `suite.digest` | `metadata` | none |
| `startedAt`, `endedAt` | `started_at`, `completed_at` | exact |
| `producer.name`, `producer.version`; for an imported run, `imported.from` ([RUN-15](../spec/03-run.md#32-runjson)) | `runner.name`, `runner.version`: the tool that ran the evaluation | exact |
| `subject` | `provider.model` when `subject.kind` is `model`; otherwise `metadata` | lossy |
| `deployment.endpoint` | `provider.api_base` | exact |
| `status: aborted`, `abortReason` | `metadata.openeval.partial: true`, reason in `metadata` | lossy |
| `execution`, `contentCapture`, `judges`, `imported.asserted` | `metadata` | none |
| a root line's `caseId` | `test_case_id` | exact |
| `trial` | `attempt` = `trial` + 1 | exact |
| a `trials` rollup line | none: EvalPort has attempts and no rollup | none |
| root `state: passed` | `passed: true` | exact |
| root `state` `failed`, `warn` | `passed: false`, with at least one scored grader | lossy: `warn` and `failed` look the same |
| root `state: inconclusive` | `passed: false` | lossy |
| root `state: scored` | `passed: false` | lossy: EvalPort has no outcome for "no rule applied", and `passed: false` beside a score reads as a verified failure (Rule 6) |
| root `state: error` | `error` (`type: runner_error`, `message` = `reason`) | lossy: AEF records no error class |
| a leaf's `path` | `grader_id` | exact |
| a leaf's `annotator.kind` or `evaluator.id` | `type` (e.g. `aef_llm`) | lossy |
| a measured leaf's `scores[0]` | `score` = `normalized`, or `value` when it is already in [0, 1] | exact; a value outside [0, 1] goes to `metadata.openeval.raw_score` |
| a measured leaf's `state` | `passed` = (`state` is `passed`) | lossy for `warn`, `inconclusive` and `scored` |
| `scores[].label` | `metadata` | none |
| a leaf in a typed absence | `score: null`, `passed: false`, `reason` | lossy: `not_measured`, `not_applicable`, `skipped`, `error` and `pending` all become one null |
| `reason` | `reason` | exact |
| `component.weight` | the grader's `weight` in the suite | exact |
| `component.required` | none | none |
| root `aggregation.strategy` | `metadata.openeval.aggregation`: `WeightedSum` → `weighted` with the same `threshold`; `MajorityVote` → `majority`; `Min` → `all` when the pass flags follow the scores | lossy: `WeightedMedian` and `CapByWorst` have no counterpart |
| `aggregation.measured`, `total`, `unmeasured`, `rulePath`, `decisive` | `metadata` | none |
| `severity`, `verdictRule`, `uncertainty`, `annotator` | `metadata` | none |
| `durationMs` | `duration_ms` (an integer) | exact to the millisecond |
| a root line's `endedAt` | `completed_at` of the Result | exact; `startedAt` has no field |
| `traceLink.traceId` | `metadata.openeval.trace_id` | lossy: the span id is lost |
| `usage` entries | `metadata.openeval.cost` | lossy: EvalPort does not fix that key's shape |
| evidence of kind `output` (capture `on`) | `actual_output` | exact when captured |
| evidence of kind `input`, `expected` (capture `on`) | the test case's `input`, `expected_output` in an exported Suite | exact when captured |
| other evidence, blobs | `metadata` | none |
| `summary.json` | `summary` (`total`, `passed`, `failed`, `skipped`, `pass_rate`, `avg_score`, `by_grader`) | lossy: `N` against `n`, verdicts, intervals and lanes are lost |
| `gates.ndjson`, overlays | none | none |
| `seal.json`, `attestation.dsse.json` | a Sigstore bundle over the exported file is EvalPort's equivalent; it signs other bytes | none: the run hash can be kept in `metadata` |

## EvalPort → AEF

| EvalPort | AEF | Fidelity |
|---|---|---|
| `run_id` | `runId` when it matches the id pattern (letters, digits, `.`, `_`, `:`, `-`; at most 128); otherwise a hash, with the original in `ext` | exact or lossy |
| `version` | `ext` | none |
| `suite_id`, `suite_version` | `suite.ref` (`suite:<suite_id>`), `suite.version` | exact; a ResultSet without `suite_version` requires a caller-supplied exact version, listed in `imported.asserted`. A ResultSet digest is not a suite identity. |
| `started_at`, `completed_at` | `startedAt`, `endedAt` in UTC | exact instant |
| `runner` | `imported.from`; `producer` is the converter (RUN-15) | exact |
| `provider.model`, `api_base`, `temperature`, `max_tokens`, `extra` | model identity supplies `subject` unless explicitly overridden; complete provider declaration is captured as supplemental document evidence | source declaration roundtrips with capture on; provider configuration is not asserted as native deployment configuration |
| (EvalPort does not say how the target was driven) | `execution.targetMode`, listed in `imported.asserted` | the converter's claim |
| `isolation`, `group` | `ext/evalport-context.json` | exact through verified import/export, including custom strings and exact sequence integers |
| `metadata.openeval.partial: true` | `status: aborted` with an `abortReason`, or `completed` with `skipped` lines for the missing cases | lossy |
| `test_case_id` | `caseId` | exact |
| `attempt` | `trial` = `attempt` − 1, on the Result's root line and on each of its grader lines ([RES-8](../spec/03-run.md#344-repeated-trials)) | exact |
| (several attempts of a case; EvalPort has no rollup) | one rollup line per case and path, with `trials`: `n` the attempts, `passed` those whose line is `passed` ([RES-8](../spec/03-run.md#344-repeated-trials)) | lossy: the rollup's `trials.aggregation` and state are the converter's reading |
| a Result | a root line | exact |
| `Result.passed`, with the graders | root `state`: `passed`; else `failed` when a scored grader failed; else `not_measured` when every grader is null | lossy: which aggregation produced `passed` is often undeclared |
| `Result.error` | `state: error`, `reason` = `type: message` | exact |
| a GraderResult | a child line, `path` = `<root path>/<grader_id>` | exact |
| `score` (a number) | `scores[].value`, `normalized` = `score` | exact |
| `metadata.openeval.raw_score` | `scores[].value` = raw, `normalized` = `score` | exact |
| `passed` with a number | `state` `passed` or `failed` | exact |
| `score: null` | `state: not_measured` with a `reason` | lossy: null does not say whether the grader was skipped, pending or failed |
| `reason` | `reason` | exact |
| the grader's `weight` | `component.weight`; `required` is `true` for `all` | lossy |
| `openeval.aggregation` | native `Own` aggregation retains the source decision; `ext.evalport.aggregationCheck` records agreement with available policy inputs | declared policies are checked; absent suite facts are explicitly unverifiable, not assumed equal-weight or default-all |
| `actual_output`; a test case's `input`, `expected_output` | blobs cited by evidence of kind `output`, `input`, `expected` ([EVD-1](../spec/03-run.md#37-evidencendjson-and-blobs)), only with `contentCapture: on` ([RUN-11](../spec/03-run.md#32-runjson)) | exact when captured |
| a test case's `context`, `retrieval_context` | blobs cited by evidence of kind `document` (capture `on`) | exact when captured |
| `duration_ms` | `durationMs` | exact |
| `Result.completed_at` | `endedAt` on the root line | exact |
| `metadata.openeval.trace_id` | `traceLink.traceId` | exact |
| `metadata.openeval.cost` | `usage` entries | lossy: EvalPort does not fix the key's shape |
| `summary` | a recomputed `summary.json` ([SUM-5](../spec/03-run.md#36-summaryjson)); each entry's `verdict` is `scored`, with no `rule`, since a ResultSet's pass rate has no run-level rule ([SUM-6](../spec/03-run.md#36-summaryjson)) | exact for the counts and rates, which the verifier recomputes |
| proposed `verdict` (`passed`, `failed`, `unverified`) | root `state` `passed`, `failed`, `not_measured` (or `inconclusive` when graders scored) | exact |
| a `.sigstore.json` bundle | retained outside the run; AEF 1.0 public-key trust policy does not accept a keyless Sigstore bundle ([SIG-4](../spec/04-integrity.md#44-signatures)) | no automatic trust transfer |

## What does not carry over

**AEF → EvalPort.** Which typed absence a leaf was, and `warn`, `inconclusive` or `scored` at the root, except in
metadata. A score's `label`. `component.required` and the aggregation's `rulePath`, `decisive` and measured share.
Trial rollups and their agreement. Severity, verdict rules, thresholds and uncertainty. `N` against `n` in the summary.
A result's start time, and the usage of each party. Evidence other than the case content, evidence digests, gate
decisions, the run's seal, overlays and signatures. `execution.targetMode`, judge calibration and `imported`.

**EvalPort → AEF.** `isolation` and `group` use a verified extension rather than native fields. The kind of non-measurement behind a null
score: skipped, pending or failed. The aggregation behind `passed`, when the producer did not declare it. The case
content, when the converter writes `contentCapture: off`.

## Worked example

The corpus line for `triage/helpfulness` of `case-17` is one of four lines of that case. All four go into one
EvalPort Result. Here is the root line; its `resultId` recomputes with [RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_479d157f3423e95d566bcbfc0c6d2461","parentResultId":null,"caseId":"case-17","path":"triage","evaluator":{"id":"composite:triage","version":"2"},"state":"failed","severity":"medium","scores":[{"metric":"triage","value":0.55,"normalized":0.55}],"verdictRule":{"expr":"triage >= threshold","threshold":0.8,"source":"run.config.thresholds.triage"},"aggregation":{"strategy":"WeightedSum","threshold":0.8,"score":0.55,"rulePath":"threshold","measured":2,"total":3,"minimumMeasuredShare":0.5,"unmeasured":{"not_measured":0,"not_applicable":1,"skipped":0,"error":0},"decisive":["r_1264eeb36620c9cbe97b71ffdbcfd331"]},"durationMs":4210,"turns":3,"evidence":["E-1"]}
```

The ResultSet below is valid against EvalPort's `schema/resultset.json`. The children `triage/policy`,
`triage/helpfulness` and `triage/groundedness` become grader results. The `not_applicable` leaf becomes
`score: null`. `WeightedSum` with threshold 0.8 becomes `weighted` with threshold 0.8. EvalPort's weighted mean of the
scored graders is (0.5 × 1.0 + 0.5 × 0.1) / 1.0 = 0.55, so `passed` is `false`, as in AEF. The facts EvalPort has no
field for go under `metadata.aef`:

```json
{
  "$schema": "https://evalport.org/schema/resultset.json",
  "version": "1.0.0",
  "suite_id": "suite:support/triage-scenarios",
  "suite_version": "4",
  "run_id": "01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10",
  "started_at": "2026-10-02T14:02:11.120Z",
  "completed_at": "2026-10-02T14:06:23.004Z",
  "runner": {"name": "agenteval-cli", "version": "1.0.0"},
  "provider": {"api_base": "http://localhost:5080"},
  "results": [
    {
      "test_case_id": "case-17",
      "grader_results": [
        {"grader_id": "triage/policy", "type": "aef_code", "score": 1.0, "passed": true,
         "metadata": {"aef": {"resultId": "r_bb4438fbedb43552a9fe55695931d5e4", "state": "passed", "evaluator": {"id": "code:refund-escalation", "version": "1"}, "component": {"weight": 0.5, "required": true}}}},
        {"grader_id": "triage/helpfulness", "type": "aef_llm", "score": 0.1, "passed": false,
         "metadata": {"aef": {"resultId": "r_1264eeb36620c9cbe97b71ffdbcfd331", "state": "failed", "evaluator": {"id": "llm:helpfulness", "version": "3"}, "component": {"weight": 0.5, "required": false}}}},
        {"grader_id": "triage/groundedness", "type": "aef_llm", "score": null, "passed": false,
         "reason": "No retrieved context was recorded for this case: nothing to ground against.",
         "metadata": {"aef": {"resultId": "r_306364ba4d940f649ea348dadfbe17de", "state": "not_applicable", "evaluator": {"id": "llm:groundedness", "version": "1"}, "component": {"weight": 0.0, "required": false}}}}
      ],
      "passed": false,
      "duration_ms": 4210,
      "metadata": {
        "openeval.aggregation": {"strategy": "weighted", "threshold": 0.8},
        "openeval.trace_id": "4bf92f3577b34da6a3ce929d0e0e4736",
        "aef": {"resultId": "r_479d157f3423e95d566bcbfc0c6d2461", "path": "triage", "state": "failed", "severity": "medium",
                "aggregation": {"strategy": "WeightedSum", "rulePath": "threshold", "measured": 2, "total": 3}}
      }
    }
  ]
}
```

Read back:

- `test_case_id` gives `caseId` `case-17`;
- each `grader_id` gives a child `path`;
- `metadata.aef.path` gives the root path.

RES-4 then gives the same four ids as the corpus: `r_479d157f3423e95d566bcbfc0c6d2461`,
`r_bb4438fbedb43552a9fe55695931d5e4`, `r_1264eeb36620c9cbe97b71ffdbcfd331` and `r_306364ba4d940f649ea348dadfbe17de`.
An importer that ignores `metadata.aef` reads the null score as `not_measured`, and gets `not_applicable` back only
from metadata.

## Isolation and group extension

`AgentEval.Results.Interop.EvalPortRunContext` preserves these run-level declarations
in `ext/evalport-context.json`, written before the run is closed and covered by its
normal seal. This adds no fields to the base AEF 1.0 run schema.

```json
{"schemaVersion":"1.0","isolation":"fresh","group":{"group_id":"sweep-1","parent_group_id":"campaign-1","role":"candidate","label":"model A","sequence":17}}
```

The implementation follows the isolation/group constraints in the
[pinned ResultSet schema](https://raw.githubusercontent.com/adhabnr-ux/evalport/d0e90c48cfa8cabceefb066dcfa757ea3f5c85f3/schema/resultset.json),
rechecked on 2026-10-14. Isolation and role remain open strings; absence stays
undeclared. Group identifiers and optional parent identifiers are nonempty strings;
self-parenting is refused. Sequence is an exact nonnegative JSON integer, with no
inferred contiguous membership or group completeness. Labels are display text,
never identity. Declaring isolation does not prove that execution enforced it.

`FromResultSet` extracts these fields from strict JSON bounded to the standard AEF
JSON limit; it does not validate other ResultSet fields. `ToBytes`/`Parse` use a
64 KiB extension limit. `WriteTo` writes through an open `AefRunWriter`.
`ApplyTo` returns a detached object and refuses conflicting destination context.
Both the numeric value and original sequence token are retained; no conversion
through binary64 or a JavaScript number is used to determine integrality.

The extension parser is deliberately closed at version 1.0. Unknown isolation/role
values are accepted, while unknown field names are refused. This preserves the
pinned schema's distinction between open vocabularies and closed objects.

`IEvalPortRunContextReader` / `EvalPortRunContextReader` provides a verified read:
it captures a bounded owned copy, verifies the base run, optionally checks the
caller's exact run hash, then parses the extension under its 64 KiB limit. The
result includes `RunId`, `RunHash`, the base `Evidence` verification report and
nullable `Context`. Null means that the sealed run has no extension; an existing
empty extension is distinguishable. Tampered, unsealed or hash-mismatched evidence
is refused. The source run is never modified. Capture is not an atomic filesystem
snapshot; verification applies to the owned copy. Cancellation is cooperative.

`AgentEval.Results.Adapters.EvalPort.EvalPortResultSet` now provides the conversion
input boundary. It uses the [vendored pinned schema](../../../evalport/1.0.0-rc.5/README.md)
offline, strict JSON at 4 MiB, detached source data and the context validation above.
It rejects duplicate case/attempt pairs (an absent attempt collides with explicit
attempt 1), repeated grader identifiers within a result, null-score graders marked
passing and passing results with no measured graders. Attempt and duration fields
must be exact integers within Int64 range. These limits are adapter refusals, not
new constraints claimed for all EvalPort producers. No output is written by parsing.

This boundary does not yet check aggregation metadata or perform date conversion:
schema `format` is an annotation in the reused validator. The importer must check
those before producing AEF evidence. It accepts the pinned schema's shape with
major version 1, and refuses unknown fields including the proposed `verdict` field.

The internal import planner now validates header and temporal mapping before output
creation. `AefEvalPortImportOptions` supplies target mode, an optional explicit
subject and a missing suite version. Without an explicit subject, a declared
provider model becomes a model subject; if neither is available, import planning
refuses. Caller-supplied subject and suite-version facts are listed as assertions.
Unsupported run identifiers are mapped to a deterministic SHA-256-based identifier,
with the source identifier retained in the header's `ext.evalport`.

Known RFC3339 offsets are normalized to UTC without rounding up to nine fractional
digits. Invalid dates, unknown offset `-00:00`, missing offsets and finer precision
are refused. Completion cannot precede start; per-result completion must lie within
the declared run interval. No completion time means a running, unsealed run.
A completed ResultSet declaring boolean `metadata.openeval.partial: true` maps to
an aborted run.

## Implemented importer and remaining work

`IAefEvalPortImporter` / `AefEvalPortImporter.Import` now accepts bounded in-memory
ResultSet bytes and writes an AEF run with the planned header and context extension.
Completed runs close and optionally receive an ingest seal. A source without a
completion time remains running and unsealed. Failed writes are discarded using
the existing adapter cleanup rules; an existing nonempty destination is refused.

Each source result becomes a root at `evalport`; grader observations become children.
Grader paths use a deterministic SHA-256 of the original grader identifier, retained
alongside its original type in `ext.evalport`. Null scores become `not_measured`;
numeric scores retain normalized and optional raw values. Captured output and grader
reasoning use blobs. Capture-off writes neither their contents nor their digests.

The implemented root mapping uses **Own** aggregation and zero-weight grader children,
preserving the source's `passed` declaration instead of pretending that an AEF
threshold reproduces arbitrary EvalPort aggregation metadata. `rulePath: threshold`
is the AEF display category for that declared Boolean decision; no numeric threshold
is asserted. This supersedes the speculative `Min`/`WeightedSum` root reconstruction
in the mapping tables for this importer. It is not a validation of the source's
aggregation calculation. Aggregation metadata is retained only with capture-on.

Explicit attempts map to zero-based trial indices; repeated cases receive
converter-computed `AllPass` rollup trees. Rollups cover observed attempts only, do
not fill gaps, and are marked `converterComputed`. Counts and agreement come from
the recorded states. No measured trials gives `not_measured`; incomplete measured
agreement without a failure gives `inconclusive`. Trial indices must fit the current
AEF writer's Int32 representation, and each rollup supports at most 1000 trials.
Duration integers beyond binary64's exact integer range are refused rather than
rounded by the writer. These are explicit adapter limits.

Root `ext.evalport.attemptDeclared` records whether the source supplied `attempt`.
This keeps an omitted attempt distinguishable from an explicit attempt 1, even
when both become trial 0 in a repeated case's AEF tree.

An execution error keeps its declared type and retryability in root metadata and
uses AEF state `error`. With capture-on, the complete source error object is stored
as document evidence cited by the root; `ext.evalport.errorEvidence` identifies the
evidence record. With capture-off, message and code content are omitted and
`contentOmitted` records that omission when applicable. Thus error classification
does not require retaining provider messages that may contain case content.

The current importer does not transfer provider configuration, arbitrary metadata,
the source summary or external signatures; conversion notes state these losses.
The AEF summary is recomputed from root pass observations/rollups without a run-level
approval rule. Complete interoperability roundtrip acceptance remains unfinished. The full W09 interoperability task is not complete.

`IAefEvalPortExporter` / `AefEvalPortExporter` now exports verified base evidence
from a bounded owned copy. It requires an intact seal, supports an optional exact
run-hash pin, and refuses overlay-bearing runs unless `IgnoreOverlays` explicitly
selects base evidence. It does not apply an effective view or transfer a signature.

Imported source run/suite/grader identifiers, declared runner/model identity,
isolation/group context and attempt presence are restored when preserved. Captured
output, grader reasoning and source error objects are read only from verified local
blobs. Capture-off or authorized withholding leaves content absent and reports the
loss. Generic AEF leaf states and paths are retained under `metadata.aef`; flattened
trees, omitted rollups and first-score-only mapping are listed in conversion notes.
Scores outside [0,1] without normalization are refused. Source score arrays remain
in grader `metadata.aef.scores`, while only the first score is projected numerically.
Scoreless AEF pass/fail assertions map to score 1/0, explicitly labeled
`metadata.aef.scoreProjection: "verdict"`; this is a Boolean projection rather than
an observed numeric metric. Typed absences remain null-score, nonpassing graders.
Non-finite values are invalid AEF evidence and are refused, not repaired on export.
Repeated attempts remain
individual results; converter rollups do not become extra EvalPort observations.

Captured text consumption and the resulting ResultSet each have a 4 MiB bound.
The exported document is checked by the pinned ResultSet validator. This is an
initial reverse projection, not completion of the full mapping matrix: remaining
generic reverse structural fidelity and further resource tests
still need acceptance.

When one AEF case has multiple distinct root paths, each case/path receives a
deterministic `aef-root-<sha256>` EvalPort test-case identifier. A suffix resolves
collisions with existing case IDs. Original case/path identity remains in each
result's `metadata.aef.caseId` and `path`. This prevents duplicate observations
without relabeling separate roots as repeated attempts or combining their verdicts.
The mapping is stable within the selected run and is reported in conversion notes.

### Aggregation checks and suite context

Before writing output, the importer checks result-level `openeval.aggregation`
against measured graders. Null scores are excluded. Supported policies are `all`,
`any`, strict `majority` (pass fraction greater than its cutoff, default 0.5), and
`weighted` (weighted score greater than or equal to the required threshold).
Malformed known policies and demonstrable pass-flag contradictions are refused.
Unknown policy fields and execution-error overrides are explicitly unverifiable.
This follows the [pinned aggregation specification](https://github.com/adhabnr-ux/evalport/blob/d0e90c48cfa8cabceefb066dcfa757ea3f5c85f3/SPEC.md#aggregation-extension-metadataopenevalaggregation).

A standalone ResultSet does not establish its suite's default policy or grader
weights. The API's optional `EvalPortAggregationContext` supplies these facts:
`Strategy` and `Threshold` describe the suite default, and `GraderWeights` contains
each resolved grader weight (explicitly 1 when the suite definition omits it).
A missing measured grader weight prevents a weighted check; it never becomes an
invented default. Result-level policies override the supplied suite default.
Supplying context adds `evalport.aggregationContext` to `imported.asserted`.
The current CLI imports standalone ResultSets without suite context.

Each root retains `ext.evalport.aggregationCheck`, including status, reason and,
when checked, measured count, expected decision, strategy and threshold. Weighted
checks also record the caller-supplied weights and computed score. Finite weights
are scaled before summation to avoid overflow; comparison uses binary64 arithmetic.
Zero total measured weight is unverifiable. These checks are retained with capture
off because they contain decision inputs rather than captured output or reasoning.
`verified` here means arithmetic agreement with the available inputs, not trusted
execution or an approval. Native AEF still uses `Own` to avoid substituting a
different AEF aggregation rule for the recorded external decision.

### Captured source declarations

With content capture on, the importer writes a structured supplemental document
blob referenced by `ext/evalport-supplement.json` and document evidence
`E-evalport-supplement`. It contains provider configuration, the declared source
summary, and run/result/grader metadata. Per-observation metadata is keyed by
case ID and effective attempt; grader metadata is keyed by grader ID. Scores,
verdicts, output and reasoning are still mapped through native AEF results and
evidence, not reconstructed from a saved ResultSet archive.

The exporter reads the supplement only from the verified owned snapshot and
within the shared 4 MiB captured-content budget. It restores declarations after
native projection, then validates the complete ResultSet. Source summary values
remain source claims; they do not replace the recomputed AEF summary. On metadata
key collisions the captured source value wins, with an explicit conversion note;
source `metadata.aef` is not an exporter attestation. External signatures are
never transferred. The supplement is omitted entirely with capture off; an
authorized withheld supplement is omitted with a note rather than fabricated.

### Resource and malformed-extension acceptance

A valid AEF seal does not establish that arbitrary `ext.evalport` data has the
converter's expected shape. Export explicitly validates origin identifiers,
attempt-presence flags and error-object shape before using them. Malformed
supplement pointers, unsupported supplement versions and mismatched or duplicate
observation identities are refused. Blob digests must contain exactly 64 lowercase
hex digits after `sha256:` before any blob path is constructed.

Captured text has a cumulative 4 MiB read budget. The final ResultSet separately
has a 4 MiB serialized JSON limit: an output blob of exactly 4 MiB cannot fit once
the result envelope is added, and JSON escaping can make smaller raw text exceed
the document limit. Oversized documents are refused rather than truncated. CLI
import/export translates strict JSON and byte-limit errors into input-error exit
code 2 and retains existing destination data.

Generic native AEF roundtrips preserve the projected EvalPort observations and,
with capture on, original `metadata.aef` declarations. They do not restore the
original AEF tree or typed states from arbitrary metadata: for example, a scored
AEF result maps to EvalPort `passed: false` and becomes a failed observation on
reimport, while its original `scored` state remains explicitly in source metadata.
Multiple root paths keep distinct projected test-case IDs without invented
attempts. This is a documented lossy projection, not an AEF identity roundtrip.

### CLI publication

```sh
agenteval aef import evalport resultset.json new-run --target-mode mocked --content-capture off
agenteval aef export-evalport new-run resultset-export.json --json
```

Import requires `--target-mode`. Supply `--subject-ref` and `--subject-kind`
together when asserting a subject; otherwise the provider model supplies it.
Use `--suite-version` when the source omits an exact version. The existing
`--content-capture on|off`, `--no-seal` and `--key` conversion controls apply;
the example explicitly disables content capture. Signing requires sealing.
An unfinished source run remains unsealed and cannot be exported by this command.

Export accepts `--run-hash`, `--suite-id`, `--policy` and `--ignore-overlays`.
Ignoring overlays explicitly selects base evidence. Conversion notes are emitted
in both text and JSON output; exported bytes do not inherit the source signature.

Both destinations must be absent with an ordinary existing parent directory.
Export must be outside the selected run. Import reads at most 4 MiB of ordinary
file input; key and policy reads are bounded at 64 KiB and 1 MiB respectively.
Completed output is published by a sibling rename without overwriting existing
paths. Cancellation or handled failure removes owned staging output. A process
termination can leave a `.ae-evalport-*` staging entry; this is not a published
run. Publication does not promise crash durability or protection against a
concurrent adversarial filesystem replacement.

Still pending: remaining ResultSet adapter coverage and roundtrip acceptance.
A directly parsed extension alone has not had its seal
checked. Group chains across multiple runs are consumer joins;
this component does not resolve parents or prove absence of cross-run cycles.
