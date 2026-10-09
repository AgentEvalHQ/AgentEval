# AEF reference tools

Python 3.12 (tested on Windows and Linux), standard library only. Run each as `python -X utf8 -I <tool>.py`.

## Checking things

| Tool | Does | Self-check |
|---|---|---|
| `aef_verify.py` | The reference verifier: runs, seals, overlay chains and views, checkpoints, lanes, signatures, paths, result ids. Written from the specification alone. | through `aef_conformance.py` |
| `aef_conformance.py` | The conformance runner ([CONF-3](../1/spec/09-conformance.md#93-running-the-corpus)): runs every vector of the corpus through an implementation and compares the results; judges the write-side vectors with the reference verifier. `--self-check` switches checks of the verifier off, and breaks writers of `aef_produce.py`, one at a time, and confirms the corpus notices each. | `--self-check` |
| `aef_produce.py` | The reference writer for the write-side vectors: computes a run's `summary.json`, writes a run from a scenario's facts (result ids, parents, trials, rollups, aggregation counts, summary), seals a run, signs a file with a PKCS#8 key. Written from the specification alone. | through `aef_conformance.py --self-check` |
| `aef_decide.py` | The decision function (spec 05, §5.4). | `--check` |
| `aef_stream.py` | The runner stream verifier and plan matching (spec 06). | `--check` |
| `aef_schema.py` | A JSON Schema 2020-12 validator with the pattern semantics AEF requires ([ENC-14], [ENC-15]). | `--self-test` (compares with the `jsonschema` package when it is installed) |
| `aef_crypto.py` | DSSE, ECDSA P-256 and Ed25519, key ids. | `--self-test` |
| `check_spec.py` | Checks that the spec, the schemas and the corpus agree: rule ids, problem codes, field names. | runs on the files |
| `schema_diff.py` | Fails on a schema change a minor version may not make ([VER-5], [CONF-5]). | `--self-test` |

## Interop (informative)

| Tool | Does | Self-check |
|---|---|---|
| `aef_interop.py` | Reference converters for two of the [interop mappings](../1/interop/README.md), written from their pages alone: `to-otel` (a run as OpenTelemetry `gen_ai.evaluation.result` events, OTLP/JSON logs), `from-otel` (OTLP/JSON logs as an imported run, sealed as `ingest`) and `to-inspect` (a run as an Inspect eval log). It follows the rules and refusals the pages state beyond their tables (OT-1 to OT-6, IN-1 to IN-5), and a refusal names its rule. | through `check_interop.py` |
| `check_interop.py` | Runs the checked examples in `1/interop/examples/`: reruns each conversion and compares the output byte for byte, checks the refusals, verifies every run with `aef_verify.py run`, checks a round trip's losses field by field against the page's "What does not carry over" list, and checks that the pages' worked-example blocks equal the examples' data. Not conformance vectors. `--update` rewrites the expected outputs. | runs on the files |

## Building the corpus and the derived files

Every expected result in the corpus is written by hand in these generators; each generator implements only what it
writes (a seal, a result id, a key id). Run them in this order; they rewrite their files byte for byte:

1. `derive_reader.py`: the reader schemas from the writer schemas ([VER-3]).
2. `build_conformance.py`: valid runs, run, encoding, seal, chain and overlay-view vectors, invalid and reader-only
   documents, checkpoint manifests, path lists, result ids.
3. `lane_vectors.py`: checkpoints with small sealed runs.
4. `signature_vectors.py`: envelopes, test keys (public and private) and trust policies, and signed runs.
5. `decision_vectors.py`, `protocol_vectors.py`: the decision function's and the runner protocol's vectors.
6. `write_vectors.py`: the write-side vectors (`summarize`, `produce`, `seal-write`, `sign`), from the runs, seals
   and keys above.
7. `pin_vectors.py`: `conformance/rulings/`, one vector per ruling made where two implementations disagreed (each of
   an existing kind).
8. `build_index.py`: `conformance/index.json`, last.
9. `gen_reference.py`: the field reference from the schemas (`--check` fails when the pages are stale; the AEF CI job runs it).

## Testing your implementation

`aef_conformance.py --command "<your program>"` drives any implementation that follows the command-line contract of
[§9.3](../1/spec/09-conformance.md#93-running-the-corpus): its table gives every operation, its arguments and its
output. `aef_verify.py` follows it, and its module docstring adds detail.

The runner checks every
corpus file against `index.json` first, so a modified corpus cannot pass. To claim conformance, run the vectors of
the classes you claim and name the corpus version (the SHA-256 of `index.json`) in the claim ([CONF-4]).
