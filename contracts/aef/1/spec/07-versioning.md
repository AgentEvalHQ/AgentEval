# 7. Versioning

## 7.1 Writers are strict, readers are tolerant

- **[VER-1]** Every JSON file and every NDJSON line carries `schemaVersion`, `MAJOR.MINOR` (this version: `1.0`),
  except the in-toto statements (`seal.json`, `overlays/seal-<nnnn>.json`), whose `_type` in-toto fixes: they carry it
  in `predicate.schemaVersion`.
- **[VER-2]** A **writer** produces documents valid against `schemas/writer/`: only known fields, only known enum
  values, its own version.
- **[VER-3]** A **reader** accepts documents valid against `schemas/reader/`: any minor of major 1, unknown fields
  ignored, unknown enum values and union kinds read as §7.3 says. The reader schemas are the writer schemas with
  exactly those relaxations (`tools/derive_reader.py` derives them); every other rule (required fields, types,
  patterns, lengths, conditional rules, prohibitions) holds for both.
- **[VER-4]** A reader **MUST** refuse a major version it does not know, with a message that says so.

## 7.2 What a minor version may change

- **[VER-5]** A minor version only **adds**: optional fields, enum values, union kinds, problem codes, rule ids,
  corpus vectors. It never removes or renames anything, makes something required, narrows a bound or a pattern, or
  changes a rule's meaning. Anything else is a new major version.
- **[VER-6]** A run may mix minors only as their files were written: a reader reads each document at its own
  `schemaVersion`. A writer writes one minor throughout a run.
- `tools/schema_diff.py` compares two versions of the writer schemas and fails on any change §VER-5 does not allow
  (§9.5).

## 7.3 Reading a value this version does not know

**[VER-8]** A reader meets values a later minor added. It **MUST** read each as follows, and never as a pass or as
better evidence than it is (each has a reader-only corpus vector):

| Field | An unknown value reads as |
|---|---|
| `results` `state` | `inconclusive`: measured, undecided, not a pass |
| `results` `severity` | `critical` |
| `run.json` `status` | not closed: the run is treated as `running` (`run-open`) |
| `run.json` `execution.targetMode` | `mocked`: not evidence about the live subject |
| `run.json` `execution.stimulus` | `other` |
| `run.json` `contentCapture` | `on`: content may be present (handle as private) |
| `run.json` `subject.kind`, evidence `kind`, judges' `mode`, annotator `kind` (`OTHER`), `usage.role`, `attack.taxonomy[].scheme` | `other`, shown as written |
| summary `verdict` | `inconclusive`: not a pass |
| `trials.aggregation`, `executionPolicy.aggregation`, `config.thresholds[].op` | shown as written (descriptive) |
| `metrics` `kind` | shown as written; it takes no part in summaries |
| `metrics` `direction` | `none` |
| `aggregation.strategy`, `aggregation.rulePath`, gate `rule.strategy` | shown as written (descriptive, [RES-6]) |
| gate `outcome` | `inconclusive` |
| gate `comparability` | `incomparable` |
| overlay `kind` | an annotation: recorded, no effect on any state |
| `by.assurance` | `self-attested` |
| seal `predicate.sealedBy` | `ingest` |
| checkpoint `state` or `outcome` | `unverifiable` ([CKP-7]) |
| lane rule `kind` | the lane's result is `not_measured` |
| a `severity` rule's `max` | the lane's result is `not_measured` |
| comparison axis | the comparison is `incomparable` ([LANE-6]) |
| decision lane `status` | `not_measured` ([DEC-2]) |
| runner event `kind` | skipped by the verifier ([STRM-1]) |
| plan `provider` or `isolation`, credential `scheme` or `purpose` | the runner refuses the plan ([PLAN-7]) |
| runner manifest `kind` or `os` | shown as written; it takes no part in matching ([PLAN-7]) |
| runner event `lane.completed` `status` | `not_measured` |

## 7.4 Deprecation

- **[VER-7]** A minor version **MAY** deprecate a field or value: it stays valid, the changelog names its
  replacement, and writers **SHOULD** stop using it. Only a new major version removes it, and not before it has been
  deprecated for at least one minor version.

## 7.5 AgentEval store v1

AgentEval's output directory before AEF (`.agenteval/`, "store v1": a workspace with per-run scenario files and
evidence) is not AEF. Mapping it is the reader's choice, and informative here:

| Store v1 | AEF 1.0 |
|---|---|
| run directory, `manifest.json` | a run folder, `run.json` (`producer` = AgentEval and its version; `execution.targetMode` from the run's provider: `mocked` when it ran a stand-in, else `live`) |
| scenario results (`EvalResult` trees) | `results.ndjson`, one line per node; `resultId` per [RES-4] |
| `MeasurementState` and labels | `state`: `pass` → `passed`, `fail` → `failed`, `warn` → `warn`, `error` → `error`, `skipped` → `skipped`, `inapplicable` → `not_applicable`, not measured → `not_measured` |
| compliance evidence and its hash chain | `evidence.ndjson` and blobs; the store's chain is not an AEF seal: a migrated run is sealed anew with `sealedBy: ingest` |

A migrated run is sealed by whoever migrates it (`ingest`) and says so: its seal shows the migration did not change
it afterwards, not that the original store was unchanged before.
