# AEF reference tools

Python 3.12 (tested on Windows and Linux), standard library only. Run each as `python -X utf8 -I <tool>.py`.

## Checking things

| Tool | Does | Self-check |
|---|---|---|
| `aef_verify.py` | The reference verifier: runs, seals, overlay chains and views, checkpoints, lanes, signatures, paths, result ids. Written from the specification alone. | through `aef_conformance.py` |
| `aef_conformance.py` | The conformance runner ([CONF-3](../1/spec/09-conformance.md#93-running-the-corpus)): runs every vector of the corpus through an implementation and compares the results. `--self-check` switches checks of the verifier off one at a time and confirms the corpus notices each. | `--self-check` |
| `aef_decide.py` | The decision function (spec 05, §5.4). | `--check` |
| `aef_stream.py` | The runner stream verifier and plan matching (spec 06). | `--check` |
| `aef_schema.py` | A JSON Schema 2020-12 validator with the pattern semantics AEF requires ([ENC-14], [ENC-15]). | `--self-test` (compares with the `jsonschema` package when it is installed) |
| `aef_crypto.py` | DSSE, ECDSA P-256 and Ed25519, key ids. | `--self-test` |
| `check_spec.py` | Checks that the spec, the schemas and the corpus agree: rule ids, problem codes, field names. | runs on the files |
| `schema_diff.py` | Fails on a schema change a minor version may not make ([VER-5], [CONF-5]). | `--self-test` |

## Building the corpus and the derived files

Every expected result in the corpus is written by hand in these generators; each generator implements only what it
writes (a seal, a result id, a key id). Run them in this order; they rewrite their files byte for byte:

1. `derive_reader.py`: the reader schemas from the writer schemas ([VER-3]).
2. `build_conformance.py`: valid runs, run, encoding, seal, chain and overlay-view vectors, invalid and reader-only
   documents, checkpoint manifests, path lists, result ids.
3. `lane_vectors.py`: checkpoints with small sealed runs.
4. `signature_vectors.py`: envelopes, test keys and trust policies, and signed runs.
5. `decision_vectors.py`, `protocol_vectors.py`: the decision function's and the runner protocol's vectors.
6. `build_index.py`: `conformance/index.json`, last.
7. `gen_reference.py`: the field reference from the schemas (`--check` in CI).

## Testing your implementation

`aef_conformance.py --command "<your program>"` drives any implementation that offers the same command line as
`aef_verify.py`:

- one command per operation: `run DIR [--policy P] [--anchors A]`, `seal DIR`, `chain DIR`, `view DIR --at TIME`,
  `checkpoint FILE`, `lanes CHECKPOINT --runs DIR [--at TIME]`, `signature ENVELOPE FILE POLICY [--payload-type T]`,
  `paths FILE`, `result-id RUNID CASEID PATH [TRIAL]`, `document SCHEMA FILE`, `decide FILE`, `match PLAN RUNNER`,
  `stream EVENTS PLAN`;
- input paths as arguments;
- the result as one JSON value on standard output, in the shapes `aef_verify.py`'s module docstring gives;
- exit status 0 when the operation ran (whatever the verdict), 2 on a usage or input error.

Problems are `[path, code]` pairs in the order of spec 03 §3.9 (codes alone for [CKP-7]). The runner checks every
corpus file against `index.json` first, so a modified corpus cannot pass. To claim conformance, run the vectors of
the classes you claim and name the corpus version (the SHA-256 of `index.json`) in the claim ([CONF-4]).
