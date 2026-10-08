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

Every expected result in the corpus is written down by hand, independently of the code that checks it, and checked
by two implementations:

- **The Python reference tools here** check all of it. `aef_verify.py` was written from the specification alone, by
  someone who had not seen the corpus generators.
- **AgentEval's .NET library** (`src/AgentEval.Results`) implements the decision function, the checkpoint manifest
  checks and the runner stream verifier. Its contract tests (`tests/AgentEval.Tests/Contracts/`) also check the
  schemas, seals and overlay chains, in test code. The rest (writing and sealing runs, lane evaluation, signatures,
  the rules across files) comes with AgentEval's dedicated AEF component.

A third implementation, in any language, is welcome: [§9](1/spec/09-conformance.md) says how to run the corpus and
claim conformance.

## Licence

This folder is licensed under the Apache License 2.0 ([`LICENSE`](LICENSE), [`NOTICE`](NOTICE)), unlike the rest of
the AgentEval repository (MIT), so other tools can implement the format freely. It is kept self-contained so it can
move to a neutral home unchanged; its own `.gitattributes` keeps the corpus byte-exact.
