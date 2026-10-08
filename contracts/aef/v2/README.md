# AEF v2: specification (draft)

**Status: draft, unreleased.** No producer writes v2 yet. "Must" below is a requirement on a conforming writer or
reader; the schemas in `schemas/` and the corpus in `conformance/` are part of this specification.

## 1. A run is one folder

A run is a self-contained folder. Its identity is in `run.json`, never in the path, so a reader finds runs by looking
for `run.json`. The conventional location is `<root>/.agenteval/runs/<yyyy>/<mm>/<runId>/`; nothing depends on it.

| File | Schema | Required |
|---|---|---|
| `run.json` | `run` | always |
| `results.ndjson` | `result`, one line each | always (empty for a run aborted before any result) |
| `metrics.json` | `metrics` | always |
| `summary.json` | `summary` | once the run is sealed |
| `evidence.ndjson` | `evidence`, one line each | when a result cites evidence |
| `gates.ndjson` | `gate-decision`, one line each | when the producer took a gate decision |
| `blobs/sha256/<ab>/<hex>` | none: raw bytes | when a line references a blob |
| `seal.json` | `seal` | once sealed |
| `attestation.dsse.json` | DSSE envelope | optional |
| `overlays/events.ndjson` | `overlay-event`, one line each | after close, when anything is decided or noted |
| `overlays/seal-<nnnn>.json` | `overlay-seal` | one per batch of overlay events |
| `traces.otlp.jsonl` | OTLP/JSON, one `ExportTraceServiceRequest` per line | optional |

A blob's file name is the SHA-256 of its bytes (64 lower-case hex), in a folder named by its first two characters.

JSON files are UTF-8 without a byte-order mark. NDJSON files hold one JSON object per line, each line ending in `\n`.

**A closed run never changes.** While `status` is `running` the producer may rewrite its files. When it closes the
run (`completed` or `aborted`) it writes their final form, and from then on nothing edits them. Everything added later
goes to `overlays/`.

## 2. Versions: writers are strict, readers are tolerant

Every JSON file and every NDJSON line carries `schemaVersion`, `MAJOR.MINOR` (this draft: `2.0`).

- A **writer** must produce documents valid against `schemas/writer/`: only known fields, only known enum values, its
  own version.
- A **reader** must accept documents valid against `schemas/reader/`: unknown fields are ignored, an unknown enum value
  is read as "other", and any minor of major 2 is accepted. A reader must refuse a major it does not know, with a
  message that says so. The reader schemas are the writer schemas with exactly those relaxations
  (`tools/derive_reader.py`); every other rule (required fields, types, patterns, conditional rules) holds for both.

## 3. `run.json`

The header: who produced the run (`producer`), what was evaluated (`subject`, `deployment`), with which cases
(`suite`, with a content `digest` when frozen and an `executionPolicy` for repeated trials), which judges, the
configuration as applied (`config.thresholds` maps a metric or result path to the rule its verdict used), when, what
content was captured (`contentCapture`: `off`, `hashes-only`, `on`) and the cost policy.

- `status` is `running`, `completed` or `aborted`. There is no `sealed` status: sealed is a fact about `seal.json`
  (present and verified), never a field.
- A `completed` run has `endedAt`. An `aborted` run has `endedAt` and `abortReason`.
- `ext` is the extension point on every document: readers ignore what they do not know there, and nothing in the
  contract depends on it.

## 4. `results.ndjson`

One line per node of the run's result tree.

### 4.1 States

`state` is one of:

| State | Meaning |
|---|---|
| `passed` | The only pass |
| `failed` | Measured, and below the rule |
| `warn` | A soft fail: not a pass |
| `inconclusive` | Measured, but the evidence does not decide (a split panel, an interval across the threshold) |
| `not_measured` | An input the evaluator needs was not there |
| `not_applicable` | The evaluator does not apply to this case |
| `skipped` | Not run |
| `error` | Did not complete |
| `pending` | Not finished yet (an open run) |

`not_measured`, `not_applicable`, `skipped` and `error` are **typed absences**: never a pass, and never a fail with a
score of 0. A line in one of these states must have a `reason`.

### 4.2 Result ids

`resultId` is deterministic, so the same run read twice gives the same ids:

```
resultId = "r_" + first 32 hex characters of SHA-256( UTF-8( runId + U+001F + caseId + U+001F + path + U+001F + trial ) )
```

`trial` is the decimal trial number, or the empty string on a line without one. `conformance/result-ids.json` holds
vectors, non-ASCII included.

### 4.3 Composites: how a verdict was reached

A composite node carries `aggregation`: the `strategy` (`WeightedSum`, `Min`, `WeightedMedian`, `CapByWorst`,
`MajorityVote`), its `threshold` and `score`, how many children were `measured` of the `total`, why the others were not
(`unmeasured`), and `rulePath`, the branch of the verdict rules that decided the state:

| `rulePath` | The state came from |
|---|---|
| `required-error` | a required child that errored |
| `nothing-measured` | no child being measured |
| `threshold` | the score against the threshold |
| `severity` | the worst severity among the required children |
| `under-covered` | too few children measured (`minimumMeasuredShare`) |

`decisive` lists the children that decided it, as the producer knew them; a reader never reverse-engineers this. Each
child has `parentResultId` and `component` (`weight`, `required`).

### 4.4 Repeated trials

When a case runs several times, each trial's lines carry `trial` (0-based), and one rollup line per case carries
`trials`: `n`, `passed`, the `aggregation` and `agree` (`false` when the trials disagreed: the case is flaky). The case's
result is the rollup line, never one trial. A line carries `trial` or `trials`, never both.

## 5. `summary.json` and `metrics.json`

`metrics.json` declares every metric: its `kind`, its `direction` (`higher_better`, `lower_better`, `none`) and its
`scale`. A metric with direction `none` is shown, never coloured as better or worse.

`summary.json` gives, per lane and metric, `n` (measured) beside `N` (asked for) and `notMeasured`, the `value` over the
`n` measured, its uncertainty (`stderr`, `ci`), the `verdict` and the `rule` it came from, and the sufficient
statistics (`sum`, `sumSq`) to pool runs. When `n` is 0, `value` is `null` and `verdict` is `not_measured`.

## 6. `evidence.ndjson` and `gates.ndjson`

An evidence record (`E-…`) is what a result cites: its `kind`, the `digest` of its content, and a `link` that is
exactly one of a blob in the run, a span (`traceId` and `spanId`), or a URI outside the run.

A gate decision is a decision the producer took when the run closed (a `--fail-on` gate, a baseline comparison): its
rule, its inputs, whether a comparison it needed was shown comparable, the `outcome` (`ship`, `no_ship`,
`inconclusive`), the exit code and the decisive results. An `incomparable` comparison never yields `ship`.

## 7. Overlays: what is added after close

`overlays/events.ndjson` is append-only. Each event has a `kind` (`approve`, `reject`, `override`, `adjudicate`,
`acknowledge`, `accept_baseline`, `waive`, `annotate`), a `target` (a run, result, requirement or checkpoint), who
(`by`, with an `assurance` of `self-attested`, `signed` or `authenticated`, shown exactly as written) and when.

- A `waive` has a `reason` and an `expires`.
- An `override` or `adjudicate` names the `target.result`, the `state` it sets, and a `reason`.

Events are sealed in **batches**. Batch *n* is the bytes of the events appended since batch *n-1*: whole lines, at
`offset` with `length`. `overlays/seal-<nnnn>.json` (1-based, four digits) is an in-toto Statement v1 whose subject is
`overlays/events.ndjson` with the SHA-256 of the batch's bytes, and whose predicate names the previous batch's seal
file and the SHA-256 of that file's bytes (`null` for batch 1). The batches must cover the events file from its first
byte to its last without gaps. A reader verifies the chain; a missing or altered batch breaks it, and the run's own
seal is unaffected.

## 8. Sealing

A run is sealed over its bytes. There is no canonical JSON: nothing is re-encoded, and the corpus includes non-ASCII
text and a CRLF inside a sealed blob to prove it.

1. **The sealed files** are every file in the run folder except `seal.json`, `attestation.dsse.json` and everything
   under `overlays/`.
2. **Each file's digest** is the SHA-256 of its exact bytes.
3. **The manifest** has one line per sealed file, ordered by path (ordinal comparison of the UTF-8 bytes, `/`
   separators), in the `sha256sum` shape:

   ```
   <sha256-hex>␠␠<size in bytes>␠␠<path>\n
   ```

4. **The run hash** is the SHA-256 of the manifest's UTF-8 bytes.
5. **`seal.json`** is an in-toto Statement v1: `_type` `https://in-toto.io/Statement/v1`; one `subject` per sealed file
   (`name` = its path, `digest.sha256` = its digest); `predicateType` `https://agenteval.dev/evidence/v2`; and a
   `predicate` with the `runId`, the `runHash`, the producer, subject, deployment, suite and judges from `run.json`,
   `closedAt`, and `sealedBy`: `producer` when the producer sealed the run, `ingest` when a host sealed it on taking
   custody of an unsealed run.
6. **Verification** recomputes every digest. Each difference is one of: a sealed file whose bytes changed (`digest`),
   a file present but not sealed (`not-sealed`), a sealed file that is gone (`missing`). A run verifies only when there
   are none, and is shown verified only after a recomputation.
7. **Signatures** are optional: `attestation.dsse.json` is a DSSE envelope whose payload is the exact bytes of
   `seal.json` (`payloadType` `application/vnd.in-toto+json`), with one or more signatures.

`conformance/seal-vectors/` holds sealed runs with their expected manifests, and runs changed after sealing (a byte
changed, a file added, a file removed) with the expected differences.

## 9. Not yet in this draft

These are specified in the design and will be added to v2 before it is released, each with its schema and vectors:

- the checkpoint manifest and the open decision function, with decision vectors;
- the run plan, the runner capability manifest and the runner event stream, with protocol vectors;
- DSSE vectors (valid, wrong key, tampered payload) with test keys;
- v1-to-v2 migration vectors;
- `views.json` and the catalog manifest.

## 10. Conformance

A writer or reader in any language conforms when it passes `conformance/`:

- every file of every run in `valid/` validates against the writer and the reader schemas;
- every document in `invalid/` is refused by the writer schema, and accepted or refused by the reader schema as its
  `expected.json` says (with the rule it breaks);
- every vector in `result-ids.json` reproduces;
- every run in `valid/` that has a `seal.json` verifies, with the manifest in `seal-vectors/<name>/`, and every changed
  run in `seal-vectors/` fails verification with exactly the expected differences;
- the overlay batches of every run in `valid/` form an unbroken chain over the whole events file.
