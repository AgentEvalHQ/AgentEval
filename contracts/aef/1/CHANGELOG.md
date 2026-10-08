# AEF 1.0 changelog

## Unreleased (draft): consolidation for 1.0

The draft known as "v2" became AEF 1.0 and went through an independent review (critic round 0: mean 5.5 of 10, two
blockers). Changes since:

- **Identity.** Identifiers, paths and `schemaVersion` are 1.0 (`contracts/aef/1/`, `$id` and predicate types under
  `/aef/1/`). Governance and a NOTICE file added.
- **The specification** is nine numbered documents with BCP 14 keywords, an id on every rule, roles and conformance
  classes, a threat model (§8) and a conformance chapter (§9). The old one-page text is gone.
- **Verification outcomes**: unsealed, intact, signed, anchored, or invalid. An intact run is never called authentic.
- **Signatures**: DSSE v1; ECDSA P-256 required, Ed25519 recommended; `keyid` is the SHA-256 of the SPKI DER; the trust
  policy is the verifier's input; an envelope needs at least one signature; no low-S rule.
- **Lanes bound to evidence**: a checkpoint lane's result is a function of its sealed runs (`threshold`, `severity`,
  `evidence-present`, `comparison` with an exact one-sided sign test). Only intact, completed, live runs are eligible.
  A checkpoint verifier finds runs by recomputed run hash and reports `run-missing`, `run-unverified`, `lane-result`,
  `lane-version`, `oldest-closed`. The checkpoint `sealed` state is gone: a decided checkpoint is signed.
- **Overlays bound to the run**: batch seals carry the run hash, events name their run, a `redact` event withholds a
  blob without breaking the seal, and the effective view is defined. New chain codes: `batch-number`, `run-id`,
  `run-hash`, `batch-invalid`, `event-id`, `target`, `unexpected-file`.
- **Encoding**: I-JSON (no duplicate members, no lone surrogates), NDJSON framing, limits, and patterns without
  lookaround where `$` is the end of the input.
- **Run fields**: `execution.targetMode` (live, replayed, scripted, mocked) is required; `contentCapture: off` keeps no
  content and no digests; no credentials in `run.json`; judge calibration; run cost.
- **Result fields**: `severity`, `durationMs`, `turns`, `attack` (technique, taxonomy ids, success), `lane`; `pending`
  is a typed absence and needs a reason; `unmeasured.errored` is now `unmeasured.error`.
- **Summary semantics** defined exactly (each entry names its `path`; `N`, `n`, `notMeasured`, `sum`, `value` are
  recomputable) and 22 cross-file problem codes (§3.9).
- **Fail-closed reading** of every unknown value (§7.3), a deprecation policy, and the store-v1 mapping (§7.5).
- **Comparability axes** are a fixed vocabulary.
- **Corpus**: rebuilt. Every vector is a folder with `expected.json` beside a `run/`; new families `runs/` (one per
  cross-file code), `encoding/`, `reader-only/`, `overlay-views/`, `lane-vectors/`, `signature-vectors/`, `paths.json`;
  `index.json` lists every vector with its classes, rules and file digests.
- **Tools**: `aef_crypto.py` (DSSE, ECDSA P-256, Ed25519), `aef_schema.py` (a JSON Schema validator with portable
  pattern semantics), `aef_verify.py` and `aef_conformance.py` (the reference verifier and runner), `schema_diff.py`,
  `gen_reference.py`, `lane_vectors.py`, `signature_vectors.py`, `build_index.py`. All standard-library Python.
- **Documentation**: a primer, a rationale and FAQ, interoperability mappings, and a field reference generated from
  the schemas.

## Earlier drafts

- The run folder and its files: `run.json`, `results.ndjson`, `metrics.json`, `summary.json`, `evidence.ndjson`,
  `gates.ndjson`, blobs, `seal.json`, overlay events and their chained batch seals.
- Writer and reader schemas (JSON Schema 2020-12); the reader schemas are derived from the writer schemas.
- Typed absences (`not_measured`, `not_applicable`, `skipped`, `error`) carry a reason; deterministic result ids;
  composite lineage (`aggregation.rulePath`, `decisive`); repeated trials with a rollup line.
- The seal over exact bytes (no canonical JSON): the manifest, the run hash, an in-toto Statement v1.
- The conformance corpus: valid runs, invalid documents with the rule each breaks, seal vectors, result-id vectors.
- The runtime-verdict profile (AEVP 0.1).
- Review of the draft: verification also checks the run hash, the run id, duplicate subjects and open runs; overlay
  assurance is a claim a reader verifies; patterns rule over `format` and guard the end of the string; run paths are
  ASCII and byte-ordered; NDJSON is LF-only; trials are integer digits; seal predicates carry `schemaVersion`;
  `contentCapture` is `off` or `on`; `unmeasured` uses the v2 state names; rules across files; every result state, a
  U+2028 inside a string, chain vectors and more seal vectors in the corpus.
- Review of the protocol: credentials are references with a name, scheme, path and purpose, delivered as environment
  variables, never in an endpoint; job.accepted binds the plan's bytes (planDigest) and runs carry provenance; a
  runner may refuse a plan (job.refused); more stream rules (plan digest, accepted twice, estimate, case and time
  limits, run hash changed, unsealed run) and vectors that catch plausible wrong implementations; plan-to-runner
  matching; plans carry judges, baseline, comparability, zone and the image's repository; providers and credential
  schemes extensible; the checkpoint verifier reports 'unverifiable' for what a later minor adds; impossible dates and
  integral numbers read the same in every implementation; [!-~] instead of \s; seal subjects cannot leave the folder.
- Review of checkpoints: the manifest records the decision's input beside its output and each run's hash and origin;
  rules across the manifest with a verifier; aborted modelled; freshness from the oldest run, resolved from the
  requirements; expiry at read time; future evidence is missing; unknown statuses fail closed; no lanes or a lane
  twice are refused; times compare at full precision; durations bounded; exact versions (no 'latest' in any case);
  lane names are ids; every pattern guards the end of the string; the predicate is checked against run.json.
- Run plans (credential references only), runner capability manifests and the runner event stream, with its
  verifier and hand-written stream vectors.
- Checkpoint manifests and the decision function (pure, with hand-written vectors); the reader derivation keeps
  `not` subtrees strict and accepts an unknown kind in a union discriminated by `kind`.
