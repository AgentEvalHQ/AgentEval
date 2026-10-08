# AEF v2 changelog

## Unreleased (draft)

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
