# AEF 1.0 identifiers

Agent Evidence Format (AEF) 1.0 uses these predicate identifiers:

| Identifier | Meaning |
|---|---|
| [`https://agenteval.dev/aef/1/evidence`](evidence/index.md) | A sealed run's evidence manifest |
| [`https://agenteval.dev/aef/1/overlay-batch`](overlay-batch/index.md) | A sealed batch of overlay events |

The separately versioned [artifact-evidence profile](../../attestation/artifact-evidence/v1/index.md)
binds an explicitly identified artifact to a sealed run. It is outside AEF 1.0.
The [runtime-verdict profile, AEVP 0.1](../../aevp/AEVP-0.1.md), retains its existing published lineage.

AEF's [writer run schema](https://agenteval.dev/aef/1/writer/run.schema.json) and
[reader run schema](https://agenteval.dev/aef/1/reader/run.schema.json) link to the other schemas in their
respective directories. The [suite definition schema](https://agenteval.dev/schemas/suites/1/definition.schema.json),
[requirement catalogue schema](https://agenteval.dev/schemas/requirements/1/catalogue.schema.json), and
[AEVP 0.1 schema](https://agenteval.dev/schemas/aevp-0.1.json) keep their own identifiers.

See the [AEF specification](https://github.com/AgentEvalHQ/AgentEval/tree/main/contracts/aef/1/spec)
for normative requirements, and the [AgentEval AEF guide](../../aef.md) for CLI usage.
These pages describe the identifiers; opening a page does not verify evidence or establish trust.
