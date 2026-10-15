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

The exporter accepts an intact sealed AEF run and projects each non-rollup root
into a Result and each descendant leaf into a GraderResult. This table describes
the implemented ResultSet adapter. It does not describe an EvalSuite exporter.

| AEF input | EvalPort output | Fidelity / boundary |
|---|---|---|
| `runId`, `suite.ref`, `suite.version` | `run_id`, `suite_id`, `suite_version` | Original EvalPort IDs are restored for imported runs; a caller can supply a missing suite ID. |
| `startedAt`, `endedAt` | `started_at`, `completed_at` | Exact represented instants. |
| Producer, preserved source runner | `runner` | Preserved source declaration takes precedence over converter identity. |
| Model subject | `provider.model` | Original provider model restored when retained; native model reference otherwise. Full subject remains in run `metadata.aef`. |
| `deployment.endpoint` | `provider.api_base` | Exact native endpoint. Preserved source provider configuration takes precedence when present. |
| Aborted status | `metadata.openeval.partial` | Boolean projection; abort reason is not an EvalPort status field. |
| Isolation/group extension | `isolation`, `group` | Exact verified extension, including original sequence number token. |
| Root `caseId`, `path`, `trial` | `test_case_id`, `attempt` | Multiple root paths get distinct deterministic IDs. Original case/path remain in metadata. Imported implicit attempt presence is preserved. |
| Root state | `passed` | True only for passed, measured observations; original state remains in metadata. |
| Leaf identity | `grader_id`, `type` | Restores preserved source grader ID/type; native path and generic type `aef` otherwise. |
| First score | `score`, `metadata.openeval.raw_score` | Uses normalized score or an in-range value. Unnormalized values outside [0,1] are refused. All source scores remain in grader metadata. |
| Scoreless pass/fail | Score 1/0 | Explicit `metadata.aef.scoreProjection: verdict`; not an observed numeric score. |
| Typed absence | Null score, false pass flag | Original typed state survives in metadata; EvalPort has no equivalent native state. |
| Captured reasoning, native reason | `reason` | Verified reasoning blob takes precedence; native reason is used only when no reasoning is declared, with capture on. Withheld reasoning is not replaced. |
| Root trace link | `metadata.openeval.trace_id` | Trace ID maps natively; full trace/span link remains in `metadata.aef.traceLink`. Leaf links remain in grader metadata. |
| Captured output evidence | `actual_output` | Exact UTF-8 text within the budget; multiple outputs for one result are refused. |
| Source error evidence, native error reason | `error` | Restores captured source error; native errors use runner_error plus captured reason. Missing content stays absent. |
| Exported observations | `summary` | Recomputed projection: total, passed, failed, skipped/unscored, measured-only pass rate. No pass rate when all observations are unscored. |
| Captured source supplement | Provider, summary, metadata | Restored as source declarations. A source summary is not an independently verified conclusion. |
| Run hash | `metadata.aef.runHash` | Provenance only; no signature over exported bytes. Source metadata collisions are explicitly reported. |
| Rollups, internal tree nodes, gates, effective overlays, signatures | No native counterpart in this projection | Omitted with conversion notes. Overlays require explicit base-evidence selection. |
| AEF execution policy, suite digest, thresholds, component rules, uncertainty, usage, summary lanes/intervals | Not converted into EvalPort policies or cost claims | These have no universally equivalent ResultSet interpretation. Current projection does not reconstruct them from arbitrary metadata. |

## EvalPort → AEF

The importer accepts a standalone ResultSet. It does not accept Suite/TestCase
content as though that content were present in the ResultSet.

| EvalPort input | AEF output | Fidelity / boundary |
|---|---|---|
| `run_id` | `runId` and origin extension | Exact valid AEF ID, otherwise deterministic hash ID with original retained. |
| `version`, `runner` | Origin extension, `imported.from` | Producer is the converter; source runner remains a declaration. |
| Suite ID/version | Typed suite reference and exact version | Missing version requires a caller assertion. ResultSet bytes never define suite identity. |
| Provider model | Model subject unless overridden | Other provider configuration is captured as source document evidence, not invented deployment identity. |
| No target execution mode | Caller-supplied `execution.targetMode` | Recorded in `imported.asserted`. |
| Start/end/result completion | Native UTC timestamps | Exact to nine fractional digits; inconsistent intervals and unsupported timestamps refused. |
| `openeval.partial` | Aborted status on completed source | No completion means running/unsealed. |
| Isolation/group | Sealed context extension | Exact declarations; does not prove actual execution isolation. |
| Case ID, attempt | Case ID, zero-based trial | Repeated cases get explicit trials; converter AllPass rollups are labeled converter-computed. |
| Result and graders | Root plus grader children | `Own` retains source decision; grader IDs map to deterministic paths and original IDs are retained. |
| Score/pass flag | Native score and passed/failed state | Null becomes not_measured. Raw score is retained alongside normalization. |
| Aggregation declaration | `ext.evalport.aggregationCheck` | Checks available result/suite inputs; contradictions refused and missing facts explicitly unverifiable. See suite-context section. |
| `openeval.trace_id` | Root `traceLink.traceId` | Exact 32 lowercase hex characters required, including with capture off. No span ID is invented. |
| Reason, actual output | Reasoning/output blobs | Capture on only. Capture off retains neither these contents nor their hashes. |
| Error | Error state, classification, optional document evidence | Message/code captured only with capture on; type/retryability remain available without content. |
| Integer duration | `durationMs` | Exact supported writer range; no silent rounding. |
| Provider configuration, metadata, source summary | Supplemental document evidence | Captured and keyed by observation/grader identity; restored as source claims. `openeval.cost` has no fixed shape and is not invented as native party usage. |
| Result observations | Recomputed AEF summary | Pass observations/rollups, with no invented run-level approval rule. Source summary is kept separately. |
| External signature | No inherited AEF trust | Source signatures do not sign the imported AEF bytes. |
| Proposed `verdict`, unsupported source fields | Refusal by pinned schema | Draft fields are not silently accepted. |

## Roundtrip acceptance

Imported ResultSet roundtrips cover original IDs, exact group context, attempts,
provider/runner declarations, captured output/reason/error content, metadata and
source summary. Native AEF roundtrips preserve projected observations and captured
source metadata; original AEF tree/result identities and typed states are not
restored from metadata. `EvalPortExporterTests`, `EvalPortNativeExportTests`,
`EvalPortBoundaryTests` and `EvalPortAggregationTests` exercise these distinctions,
including independent reference AEF verification.

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

This parser does not check aggregation metadata or perform date conversion:
schema `format` is an annotation in the reused validator. The importer checks
those before producing AEF evidence. The parser accepts the pinned schema's shape with
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

## Implemented conversion profile

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
is asserted. This representation does not itself validate the source's aggregation
calculation; the separate aggregation checks are described below. Original aggregation
metadata is retained only with capture-on, while the check report is retained in both modes.

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

With capture on, the importer retains provider configuration, arbitrary metadata
and source summary as supplemental source declarations. External signatures and
trust do not transfer; conversion notes distinguish these facts.
The AEF summary is recomputed from root pass observations/rollups without a run-level
approval rule. This profile is covered by the roundtrip and negative-case acceptance tests.

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
lossy reverse projection: it preserves exported observations, not the original
AEF structure. The mapping tables and resource tests define this boundary.

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

A directly parsed extension alone has not had its seal
checked. Group chains across multiple runs are consumer joins;
this component does not resolve parents or prove absence of cross-run cycles.
