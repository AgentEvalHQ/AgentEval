# AEF: the Agent Evidence Format

AEF is an open format for the evidence an AI-agent evaluation produces: what was run against what, every result with
its typed absences and lineage, the gate decisions taken on it, the human decisions added later, and a seal over the
exact bytes, so any reader can recompute what it was told.

**Status: draft, unreleased.** AEF v2 is being specified here. No AgentEval command writes it yet: the CLI still writes
the v1 store (`.agenteval/`, `src/AgentEval.DataLoaders/Output/Schema/v1/`). The producers, the reader and the sealer
in .NET come next; until a release says otherwise, any part of v2 can change.

## Layout

| Path | What |
|---|---|
| [`v2/README.md`](v2/README.md) | The normative specification: the run folder, every file, the rules, the seal |
| `v2/schemas/writer/` | JSON Schema 2020-12, strict: what a producer must write |
| `v2/schemas/reader/` | The same schemas made tolerant (unknown fields, unknown enum values, any 2.x minor), derived by `tools/derive_reader.py` |
| `v2/conformance/` | The corpus every writer, reader and verifier must pass: `valid/` and `invalid/` documents, `seal-vectors/`, `chain-vectors/`, `result-ids.json`, `checkpoints/`, `decision-vectors/`, `protocol/` (plans, runners, matching, streams) |
| `v2/CHANGELOG.md` | Changes to v2 |
| `profiles/runtime-verdict/` | The evidence attached to runtime verdicts (AEVP 0.1) |
| `tools/` | `build_conformance.py`, `decision_vectors.py`, `protocol_vectors.py` (write the corpus), `derive_reader.py` (writes the reader schemas), `aef_decide.py`, `aef_stream.py` (reference implementations; `--check` runs them on the corpus) |

Every expected result in the corpus is written down independently of the code that checks it: the seals and result
ids by the Python generator, the decisions, stream problems, matching answers and verification problems by hand. The
.NET tests (`tests/AgentEval.Tests/Contracts/`, with `src/AgentEval.Results`) check all of it; the Python references
(`aef_decide.py`, `aef_stream.py`) check the decisions and the protocol too.

## Licence

This folder is licensed under the Apache License 2.0 ([`LICENSE`](LICENSE)), unlike the rest of the repository (MIT),
so that other tools can implement the format freely. It is kept self-contained so it can move to a neutral repository
unchanged: its own `.gitattributes` keeps the corpus byte-exact. Two links point outside it until then: the AEVP
specification (`docs/aevp/`) and the .NET tests.
