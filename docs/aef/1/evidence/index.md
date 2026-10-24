# AEF 1.0 run evidence predicate

Predicate identifier: `https://agenteval.dev/aef/1/evidence`

This identifier names the predicate in an AEF 1.0 run's `seal.json`, an in-toto Statement v1.
The statement's subjects identify the sealed files and their SHA-256 digests. Its predicate records
the run identity and run hash, execution identities, closure and sealing times, and who sealed it.

The normative definition is **[SEAL-5]** in
[AEF 1.0 integrity and trust](https://github.com/AgentEvalHQ/AgentEval/blob/main/contracts/aef/1/spec/04-integrity.md).
The [writer seal schema](https://agenteval.dev/aef/1/writer/seal.schema.json) and
[reader seal schema](https://agenteval.dev/aef/1/reader/seal.schema.json) define the document shapes.

An intact manifest is distinct from a verified signer or an authorization decision. Readers must apply
the specification's integrity checks and any applicable trust policy; this identifier is not an approval.

See [AEF identifiers](../index.md) and the [AgentEval AEF guide](../../../aef.md).
