# Implementations of AEF and their conformance claims

A conformance claim names the implementation and its version, the AEF version, its classes (with the Run verifier
level, and a Sealer's signing algorithms) and the corpus it passed in full ([CONF-4](1/spec/09-conformance.md#94-claiming-conformance)).
Until the `aef-1.0` tag the corpus changes with the text, so the claims below are re-established by CI on every change
to this folder (`.github/workflows/aef.yml` in the AgentEval repository) rather than pinned to one digest; at the tag,
each names that tag's corpus digest (the SHA-256 of `1/conformance/index.json`).

| Implementation | Language | Classes claimed | Run verifier level | Signs with | How it is checked |
|---|---|---|---|---|---|
| **aef-tools**, the reference tools in [`tools/`](tools/README.md) | Python 3, standard library | Producer, Sealer, Reader, Run verifier, Overlay verifier, Checkpoint verifier, Decision engine, Stream verifier; Runner through a minimal scripted runner (`aef_runner.py`) | signed | ECDSA P-256, Ed25519 | `aef_conformance.py` (every vector) and `--self-check` (every check switched off once must be noticed); `check_runner.py` for the runner |
| **agenteval-results**, `AgentEval.Results` in the AgentEval repository | C# (.NET 8, .NET 10), base class library only | Producer, Sealer, Reader, Run verifier, Overlay verifier, Checkpoint verifier, Decision engine, Stream verifier | signed | ECDSA P-256 (it verifies both) | its driver `tests/AgentEval.Results.Conformance` through `aef_conformance.py --command` (the CI job's `aef-dotnet` step, on pull requests) |

**Independence.** The two were written apart, each from the specification text alone: neither reads the other's
code. Where they disagreed on a crafted input, the text was ruled and a vector added (`1/conformance/rulings/`, the
[changelog](1/CHANGELOG.md)).

**The Runner class** has one implementation, the reference's scripted runner (it takes only plans asking for the
`scripted` target mode, and the stream verifier finds nothing in its jobs), so it is released *at risk* in 1.0
([§9.1](1/spec/09-conformance.md#91-classes)): a second, independent runner is wanted.

**Add yours.** Open an issue in the AgentEval repository with a claim in CONF-4's form and the output of
`tools/aef_conformance.py --command "<your implementation>"` for the classes you claim. The first independent
implementation that passes a class in full is offered the second editor seat ([GOVERNANCE.md](GOVERNANCE.md)).
