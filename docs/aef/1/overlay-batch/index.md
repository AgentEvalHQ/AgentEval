# AEF 1.0 overlay batch predicate

Predicate identifier: `https://agenteval.dev/aef/1/overlay-batch`

This identifier names the predicate in an AEF 1.0 overlay batch seal. The statement's subject is
`overlays/events.ndjson`, with the digest of the batch's bytes. The predicate binds the batch number,
run identity and run hash, byte offset and length, and the preceding batch seal where required.

The normative definition is **[OVL-4]** in
[AEF 1.0 integrity and trust](https://github.com/AgentEvalHQ/AgentEval/blob/main/contracts/aef/1/spec/04-integrity.md).
The [writer overlay-seal schema](https://agenteval.dev/aef/1/writer/overlay-seal.schema.json) and
[reader overlay-seal schema](https://agenteval.dev/aef/1/reader/overlay-seal.schema.json) define the document shapes.

Verification includes the covered bytes and batch chain. A batch seal does not replace verification
of the original run or establish signer trust by itself.

See [AEF identifiers](../index.md) and the [AgentEval AEF guide](../../../aef.md).
