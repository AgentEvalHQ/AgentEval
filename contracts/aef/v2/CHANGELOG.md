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
- Checkpoint manifests and the decision function (pure, with hand-written vectors); the reader derivation keeps
  `not` subtrees strict and accepts an unknown kind in a union discriminated by `kind`.
