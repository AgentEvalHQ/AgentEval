# AEF: the AgentEval Evidence Format

AEF is an open file format for the evidence an AI-agent evaluation produces: what was run against what, every result
with its typed absences, the gate and release decisions taken on it, the human decisions added later, and seals and
signatures over the exact bytes, so anyone can check what they are told without trusting the tool that told them.

**Status: AEF 1.0 is a draft, unreleased.** It is planned to be released together with AgentEval 1.0, which will be
its first producer. Until that release any part of it may change. AgentEval's current command line still writes its
older output directory (`.agenteval/`, "AgentEval store v1", which predates AEF; [§7.5](1/spec/07-versioning.md)).

## Where to start

- [AEF 1.0](1/README.md): the specification, the schemas, the conformance corpus, the primer.
- [Governance](GOVERNANCE.md): who decides, how to propose a change, how versions are released.

## Layout

| Path | What |
|---|---|
| `1/` | AEF major version 1: `spec/` (normative), `schemas/writer/` and `schemas/reader/`, `conformance/`, primer, reference, interop mappings |
| `profiles/runtime-verdict/` | The evidence attached to runtime verdicts (AEVP 0.1), a profile on top of AEF |
| `tools/` | Python 3 reference tools, standard library only: the corpus generators, `derive_reader.py`, the reference verifier `aef_verify.py`, the decision function `aef_decide.py`, the stream verifier `aef_stream.py`, the conformance runner `aef_conformance.py`, `schema_diff.py`, and `aef_crypto.py` (DSSE, ECDSA P-256, Ed25519) |
| `GOVERNANCE.md`, `LICENSE`, `NOTICE` | How AEF is run, and its licence |

## Two implementations

Every expected result in the corpus is written down independently of the code that checks it: by hand, or by a
generator in `tools/` that implements only the rule it writes (a seal, a result id), and cross-checked by a second,
independent implementation ([§9.2](1/spec/09-conformance.md#92-the-corpus)). Two implementations, written apart from
each other, pass every vector kind through the command-line contract of
[§9.3](1/spec/09-conformance.md#93-running-the-corpus):

- **The Python reference tools here.** `aef_verify.py` was written from the specification alone, by someone who had
  not seen the corpus generators.
- **AgentEval's .NET library** (`src/AgentEval.Results` in the AgentEval repository), written from the text alone.
  Its driver (`tests/AgentEval.Results.Conformance/`, run by `run-corpus.sh`) covers every operation of the contract:
  runs, seals, overlay chains and views, signatures, checkpoints and lanes, the decision function, documents and
  plans, matching, streams and plan conformance. Where the two implementations disagreed, the text was ruled and a
  vector added ([changelog](1/CHANGELOG.md)).

Every operation of the contract checks what it is given; none writes. AgentEval's run writer, sealer and overlay
writer are checked outside the contract, by the reference verifier reading what they write
(`tests/AgentEval.Results.Conformance/writer-crosscheck.sh`).

A third implementation, in any language, is welcome: [§9](1/spec/09-conformance.md) says how to run the corpus and
claim conformance.

## Licence

This folder is licensed under the Apache License 2.0 ([`LICENSE`](LICENSE), [`NOTICE`](NOTICE)), unlike the rest of
the AgentEval repository (MIT), so other tools can implement the format freely. It is kept self-contained so it can
move to a neutral home unchanged; its own `.gitattributes` keeps the corpus byte-exact.
