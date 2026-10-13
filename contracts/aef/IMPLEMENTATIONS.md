# Implementations of AEF and their conformance claims

A conformance claim names the implementation and its version, the AEF version, its classes (with the Run verifier
level, and a Sealer's signing algorithms) and the corpus it passed in full ([CONF-4](1/spec/09-conformance.md#94-claiming-conformance)).
Until the `aef-1.0` tag the corpus changes with the text, so the claims below are re-established on every change to
this folder rather than pinned to one digest: by CI (`.github/workflows/aef.yml` in the AgentEval repository) once it
runs on GitHub (its first run is pending: every step is run locally before each commit until then). At the tag, each
claim names that tag's corpus digest (the SHA-256 of `1/conformance/index.json`).

| Implementation | Language | Classes claimed | Run verifier level | Signs with | How it is checked |
|---|---|---|---|---|---|
| **aef-tools**, the reference tools in [`tools/`](tools/README.md) | Python 3, standard library | Producer, Sealer, Reader, Run verifier, Overlay verifier, Checkpoint verifier, Decision engine, Stream verifier; Runner through a minimal scripted runner (`aef_runner.py`) | signed | ECDSA P-256, Ed25519 | `aef_conformance.py` (every vector, the Runner's `job` vectors among them) and `--self-check` (every check switched off once must be noticed); `check_runner.py` for what the `job` vectors do not reach |
| **agenteval-results**, `AgentEval.Results` in the AgentEval repository | C# (.NET 8, .NET 9, .NET 10), base class library only | Producer, Sealer, Reader, Run verifier, Overlay verifier, Checkpoint verifier, Decision engine, Stream verifier; Runner through a scripted runner (`AefScriptedRunner`) | signed | ECDSA P-256 (it verifies both) | its driver `tests/AgentEval.Results.Conformance` through `aef_conformance.py --command` (the CI job's `aef-dotnet` step, on pull requests), the Runner's `job` vectors among them |

**Independence.** The two were written apart, each from the specification text alone: neither reads the other's
code. Where they disagreed on a crafted input, the text was ruled and a vector added (`1/conformance/rulings/`, the
[changelog](1/CHANGELOG.md)).

**The Runner class** has two implementations that pass its vectors, written apart: the reference's scripted runner and
AgentEval's (`AefScriptedRunner`, from the text alone), so it is not released at risk
([§9.1](1/spec/09-conformance.md#91-classes)). The corpus's job vectors use `scripted` targets. AgentEval's CLI additionally drives `live` text-dataset
plans through `AefJobRunner`, with explicit per-purpose credentials, sequential case admission, input/output/call
caps and deadlines. This implementation supports existing OpenAI-compatible and Azure endpoints, local process
isolation, and environment credential references; unsupported providers and unbounded targets are refused.

The live extension is checked on .NET 8 and 10 by
[`runner-crosscheck.sh`](../../tests/AgentEval.Results.Conformance/runner-crosscheck.sh) and
[`runner-controls-crosscheck.py`](../../tests/AgentEval.Results.Conformance/runner-controls-crosscheck.py):
loopback HTTP, credentials, budgets, interruptions, dry-run side effects, signed runs, and independent stream/run/
plan-conformance verification. The original scripted vectors remain unchanged. These integration checks complement
the class's corpus claim; there is no claim of a second independent live executor.

[`s1-exit-crosscheck.sh`](../../tests/AgentEval.Results.Conformance/s1-exit-crosscheck.sh) checks signed native eval,
redteam and benchmark runs for one subject/version, then their three-lane signed checkpoint with both verifiers.


**Add yours.** Open an issue in the AgentEval repository with a claim in CONF-4's form and the output of
`tools/aef_conformance.py --command "<your implementation>"` for the classes you claim. The first independent
implementation that passes a class in full is offered the second editor seat ([GOVERNANCE.md](GOVERNANCE.md)).
