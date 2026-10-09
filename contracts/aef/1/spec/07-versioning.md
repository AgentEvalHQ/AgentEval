# 7. Versioning

## 7.1 Writers are strict, readers are tolerant

- **[VER-1]** Every JSON file and every NDJSON line carries `schemaVersion`, `MAJOR.MINOR` in decimal without
  leading zeros (this version: `1.0`; never `1.00`, so a minor is compared as the number it reads),
  except the in-toto statements (`seal.json`, `overlays/seal-<nnnn>.json`), whose `_type` in-toto fixes: they carry it
  in `predicate.schemaVersion`. Files whose format AEF does not own carry none: DSSE envelopes, OTLP lines, blobs and
  `ext/`; a trust policy, a caller's input, may ([SIG-4]).
- **[VER-2]** A **writer** produces documents valid against `schemas/writer/`: only known fields, only known enum
  values, its own version.
- **[VER-3]** A **reader** accepts documents valid against `schemas/reader/`: any minor of major 1, unknown fields
  ignored, unknown enum values and union kinds read as §7.3 says. The reader schemas are the writer schemas with
  exactly those relaxations (`tools/derive_reader.py` derives them); every other rule (required fields, types,
  patterns, lengths, conditional rules, prohibitions) holds for both.
- **[VER-4]** A reader **MUST** refuse a major version it does not know, with a message that says so.

## 7.2 What a minor version may change

- **[VER-5]** A minor version only **adds**: optional fields, enum values, union kinds, problem codes (a new code
  applies only to what that minor adds: a file valid under an earlier minor stays valid), rule ids, corpus vectors. It never removes or renames anything, makes something required, narrows a bound or a pattern, or
  changes a rule's meaning. A minor never changes what an existing lane rule computes through a field it adds to a
  run: a new way to compute a lane is a new rule kind or a new rule member, which earlier verifiers report as
  `unverifiable` ([CKP-8]). Anything else is a new major version.
- **[VER-9] Closed enums.** Three enums are closed for major 1, because the rules across files (§3.9) compute with
  them, and a reader could not check a run holding a value it does not know: a result's `state`, `run.json`'s
  `status`, and a metric's `kind`. A new value in one of them is a new major version. Their reader schemas keep the
  enum (`tools/derive_reader.py`), so a value outside it is a `schema` problem, never read as another value. A trust
  policy is closed for readers as a whole ([SIG-4]): it is an input, and a verifier cannot honour a restriction it
  does not know.
- **[VER-6]** A run may mix minors only as their files were written: a reader reads each document at its own
  `schemaVersion`. A writer writes one minor throughout a run.
- `tools/schema_diff.py` compares two versions of the writer schemas and fails on any change [VER-5] does not allow
  (§9.5).

## 7.3 Reading a value this version does not know

**[VER-8]** A reader meets values a later minor added. A value is **known** when this version's writer schema accepts
it at that field (a value of its enum, or one a pattern there allows, such as a plan's `ci:<name>` provider); any
other value is unknown. It **MUST** read each unknown value as follows, and never as a pass or as
better evidence than it is (each has a reader-only corpus vector):

| Field | An unknown value reads as |
|---|---|
| `results` `severity` | `critical` |
| `run.json` `execution.targetMode` | `mocked`: not evidence about the live subject |
| `run.json` `execution.stimulus` | `other` |
| `run.json` `contentCapture` | `on`: content may be present (handle as private) |
| `run.json` `subject.kind`, evidence `kind`, judges' `mode`, annotator `kind` (`OTHER`), `usage[].role` (on result lines and in `summary.json`), `attack.taxonomy[].scheme` | `other`, shown as written |
| summary `verdict` | `inconclusive`: not a pass |
| `trials.aggregation`, `executionPolicy.aggregation`, `config.thresholds[].op` | shown as written (descriptive) |
| `metrics` `direction` | `none` |
| `aggregation.strategy`, `aggregation.rulePath` | shown as written (descriptive, [RES-6]) |
| gate `outcome` | `inconclusive` |
| gate `comparability` | `incomparable` |
| overlay `kind` | an annotation: recorded, no effect on any state |
| `by.assurance` and every `assurance` of a trusted identity (overlay events, a decision input's exceptions, a checkpoint's `budget.approvedBy`) | `self-attested`, whatever the value, known or not, until a signature verifies it ([OVL-3]) |
| seal `predicate.sealedBy` | `ingest` |
| checkpoint `state` or `outcome` | `unverifiable` ([CKP-7]) |
| lane rule `kind` | the lane's result is `not_measured` |
| a `severity` rule's `max` | the lane's result is `not_measured` |
| comparison axis | the comparison is `incomparable` ([LANE-6]) |
| a decision input's `lanes[].result.status` | `not_measured` ([DEC-2]) |
| a decision's `lanes[].status` or `outcome` | `unverifiable`: the decision cannot be recomputed ([CKP-7]) |
| a `threshold` rule's `op` | the lane's result is `not_measured` |
| runner event `job.failed` `limit` | shown as written |
| runner event `kind` | skipped by the verifier ([STRM-1]) |
| plan `provider`, `isolation` or `contentCapture`, credential `scheme` or `purpose` | the runner refuses the plan ([PLAN-7]) |
| runner manifest `kind` or `os` | shown as written; it takes no part in matching ([PLAN-7]) |
| runner event `lane.completed` `status` | `not_measured` |

## 7.4 Deprecation

- **[VER-7]** A minor version **MAY** deprecate a field or value: it stays valid, the changelog names its
  replacement, and writers **SHOULD** stop using it. Only a new major version removes it, and not before it has been
  deprecated for at least one minor version.

## 7.5 AgentEval store v1

AgentEval's output directory before AEF (`.agenteval/`, "store v1": a workspace with per-run scenario files and
evidence) is not AEF. Mapping it is the reader's choice, and informative here, except the case ids and paths of the
results: a migration uses them, so two migrations of one store give the same result ids ([RES-4]). Those bind a tool
that migrates store v1 runs, and no conformance class of §9.1.

**The run**

| Store v1 | AEF 1.0 |
|---|---|
| run directory, `manifest.json` | a run folder, `run.json`, with the store's run id as `runId`: `producer` is the tool that migrates it ([RUN-15]); `imported.from` is `agenteval store v1 (AgentEval <version>)`, the version as the manifest records it (the assembly version, such as `0.43.0.0`); `imported.asserted` lists every field the migration supplied rather than read |
| (store v1 records no provider) | `execution.targetMode`, supplied, so listed in `imported.asserted`: `mocked`, unless the person migrating knows the run drove the live subject (`live`) |
| (store v1 records no capture policy) | `contentCapture`, supplied and listed: what the migration keeps ([RUN-11]) |
| the subject's name and kind | `subject.ref`: `agent:<name>` or `workflow:<name>`, the name encoded as [ENC-13] says; `subject.kind`: `agent` or `workflow` |
| the subject's version (free text) | `subject.version` when it is an exact version ([ENC-10]); otherwise none, and the run serves no checkpoint lane |
| the manifest's timestamp and duration | `startedAt`, and `endedAt` = `startedAt` + the duration |
| a run that never completed (no `summary.json`, or the manifest's verdict still `PENDING`) | `status: aborted`, with an `abortReason`; `endedAt` = `startedAt`; `status` and `endedAt` listed in `imported.asserted` |
| the judges the scenarios' comparability facts name | `judges[]` (`model`, `rubricDigest`), in order of first appearance |
| the manifest's other fields (solution, subject details, run kind, harness, seed, git, AgentEval version, environment, content hash) | `run.json` `ext."agenteval.store-v1"`; never the machine name, which identifies a person's computer |
| (store v1 records no suite and no deployment) | none: `run.json` has no `suite` and no `deployment` |

**The results**

| Store v1 | AEF 1.0 |
|---|---|
| a `ScenarioResult` | a root line: `caseId` the scenario's id, `path` `scenario`, `lane` the run's kind (`main` when the kind is not an id); `evaluator` the eval's key and version from the scenario's `ComparabilityFacts`, else its eval-result tree's metric key and version, else `agenteval.scenario` with no version |
| each assertion of the scenario | a child line at `scenario/assertions/<n>`, `<n>` its 1-based position in the scenario (names repeat, and may hold `/`); `evaluator.id` the assertion's name |
| a scenario with assertions | `aggregation`: `strategy: Own`, `rulePath: threshold` (`nothing-measured` when the scenario is a typed absence), `score` and `threshold` the scenario's score and the eval's effective bar, no `decisive`; each child `component: {weight: 0, required: false}` ([RES-6]). Store v1 records the scenario's own verdict and its assertions beside it, not how one led to the other |
| `MeasurementState` and labels of the eval-result tree | `state`: `pass` → `passed`, `fail` → `failed`, `warn` → `warn`, `error` → `error`, `skipped` → `skipped`, `inapplicable` → `not_applicable`, not measured → `not_measured`; `severity` the score's, on a `failed` or `warn` line |
| a label outside those six (free text) | read by the score's pass flag: `passed` or `failed`, as AgentEval's own report reads it |
| a scenario with no eval-result tree | `passed` or `failed` from its pass flag (`ScenarioResult.Passed`), with no `severity` |
| an assertion's `Inconclusive` (it could not decide) | `not_measured`, with the assertion's message as `reason`: AEF's `inconclusive` is measured, and this is not |
| the scenario's score and metrics | `scores` on a measured root: `score`, and each metric under its own name, declared with `direction: none` |
| its input and output, and the run's agent trace (`traces/agent-trace.json`) | only with `contentCapture: on` ([RUN-11]): evidence of kind `input`, `output` (`other` when the output holds the eval-result tree) and `transcript`; the trace is cited by the root of the scenario it names |
| its estimated cost | a `usage` entry with role `other` on the root |
| its other fields (name, pass flag, score, comparability facts; its input's digest only with `contentCapture: on`) | the root's `ext."agenteval.store-v1"` |
| compliance evidence (`compliance/<regulation>/<subject>/<ts>/evidence.json`) | each document whose `sourceRun.runId` is the run's id belongs to the run: an evidence record of kind `compliance_artifact`, with the document as its blob, cited by the root of each scenario its controls name. The blob **SHOULD** be the document's original bytes; a migration that can only read a re-serialized form says so in the record's `description`. The store's hash chain is not an AEF seal |

**The summary**

| Store v1 | AEF 1.0 |
|---|---|
| `summary.json` | one lane, named after the run's kind (`main` when the kind is not an id), with two entries at `scenario`: `pass` (kind `rate`) and `score`. Neither has a rule, so each is `scored` ([SUM-6]): store v1's verdict is the run's, not an entry's |
| its verdict, stats and metrics | `summary.json` `ext."agenteval.store-v1"` |
| its cost and tokens | `cost`, and one `usage` entry with role `other`: store v1 does not say whose tokens they are |

A migrated run is an imported run ([RUN-15]): sealed by whoever migrates it (`ingest`), and it says so. Its seal shows
the migration did not change it afterwards, not that the original store was unchanged before.
