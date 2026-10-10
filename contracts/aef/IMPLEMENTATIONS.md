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
| **agenteval-results**, `AgentEval.Results` in the AgentEval repository | C# (.NET 8, .NET 10), base class library only | Producer, Sealer, Reader, Run verifier, Overlay verifier, Checkpoint verifier, Decision engine, Stream verifier; Runner through a scripted runner (`AefScriptedRunner`) | signed | ECDSA P-256 (it verifies both) | its driver `tests/AgentEval.Results.Conformance` through `aef_conformance.py --command` (the CI job's `aef-dotnet` step, on pull requests), the Runner's `job` vectors among them |

**Independence.** The two were written apart, each from the specification text alone: neither reads the other's
code. Where they disagreed on a crafted input, the text was ruled and a vector added (`1/conformance/rulings/`, the
[changelog](1/CHANGELOG.md)).

**The Runner class** has two implementations that pass its vectors, written apart: the reference's scripted runner and
AgentEval's (`AefScriptedRunner`, from the text alone), so it is not released at risk
([§9.1](1/spec/09-conformance.md#91-classes)). Both give only the `scripted` target mode, so no vector tests what the
requirements ask of a runner that drives a live target: that it gives each credential to the process its purpose
names ([PLAN-3](1/spec/06-runners.md#61-run-plans)), and holds a live case within the bounds it states
([PLAN-9](1/spec/06-runners.md#65-running-a-job)). An erratum found there is fixed as [GOVERNANCE.md](GOVERNANCE.md)
says.

**Add yours.** Open an issue in the AgentEval repository with a claim in CONF-4's form and the output of
`tools/aef_conformance.py --command "<your implementation>"` for the classes you claim. The first independent
implementation that passes a class in full is offered the second editor seat ([GOVERNANCE.md](GOVERNANCE.md)).
