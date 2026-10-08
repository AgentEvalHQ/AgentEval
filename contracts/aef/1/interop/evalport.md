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
| `suite_id`, `suite_version` | `suite.ref` (`suite:<suite_id>`), `suite.version` | exact; when `suite_version` is absent, the converter computes one from the suite file's digest and lists `suite.version` in `imported.asserted` ([RUN-15](../spec/03-run.md#32-runjson)) |
| `started_at`, `completed_at` | `startedAt`, `endedAt` in UTC | exact instant |
| `runner` | `imported.from`; `producer` is the converter (RUN-15) | exact |
| `provider.model`, `api_base`, `temperature`, `max_tokens` | `subject` (`kind: model`), `deployment.endpoint`, `config` | lossy: a ResultSet names a model, which may be one part of the subject |
| (EvalPort does not say how the target was driven) | `execution.targetMode`, listed in `imported.asserted` | the converter's claim |
| `isolation`, `group` | `ext` | none |
| `metadata.openeval.partial: true` | `status: aborted` with an `abortReason`, or `completed` with `skipped` lines for the missing cases | lossy |
| `test_case_id` | `caseId` | exact |
| `attempt` | `trial` = `attempt` − 1 | exact |
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
| `openeval.aggregation` | `aggregation.strategy`: `weighted` → `WeightedSum`, `majority` → `MajorityVote`, `all` → `Min`; `any` has no counterpart | lossy |
| `actual_output`; a test case's `input`, `expected_output` | blobs cited by evidence of kind `output`, `input`, `expected` ([EVD-1](../spec/03-run.md#37-evidencendjson-and-blobs)), only with `contentCapture: on` ([RUN-11](../spec/03-run.md#32-runjson)) | exact when captured |
| a test case's `context`, `retrieval_context` | blobs cited by evidence of kind `document` (capture `on`) | exact when captured |
| `duration_ms` | `durationMs` | exact |
| `Result.completed_at` | `endedAt` on the root line | exact |
| `metadata.openeval.trace_id` | `traceLink.traceId` | exact |
| `metadata.openeval.cost` | `usage` entries | lossy: EvalPort does not fix the key's shape |
| `summary` | a recomputed `summary.json` ([SUM-5](../spec/03-run.md#36-summaryjson)) | lossy: the entries' `verdict` has no source (see [Still open](#still-open)) |
| proposed `verdict` (`passed`, `failed`, `unverified`) | root `state` `passed`, `failed`, `not_measured` (or `inconclusive` when graders scored) | exact |
| a `.sigstore.json` bundle | outside the run; AEF accepts Sigstore as a trust-policy input ([SIG-4](../spec/04-integrity.md#44-signatures)) | lossy |

## What does not carry over

**AEF → EvalPort.** Which typed absence a leaf was, and `warn`, `inconclusive` or `scored` at the root, except in
metadata. A score's `label`. `component.required` and the aggregation's `rulePath`, `decisive` and measured share.
Trial rollups and their agreement. Severity, verdict rules, thresholds and uncertainty. `N` against `n` in the summary.
A result's start time, and the usage of each party. Evidence other than the case content, evidence digests, gate
decisions, the run's seal, overlays and signatures. `execution.targetMode`, judge calibration and `imported`.

**EvalPort → AEF.** `isolation` and `group`: AEF has no field for either. The kind of non-measurement behind a null
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

## Still open

- **A summary entry for a pass rate with no run-level rule.** A `summary.json` entry needs a `verdict`, and none of its
  values (`passed`, `failed`, `warn`, `inconclusive`, `not_measured`; [SUM-6](../spec/03-run.md#36-summaryjson))
  means "no rule was applied". A ResultSet's `summary` has a pass rate and no rule for it.
- **`isolation` and `group`.** AEF has no field for a ResultSet's trial isolation or for its membership in a group of
  sibling runs.
