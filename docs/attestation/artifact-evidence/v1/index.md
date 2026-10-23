# Artifact-evidence profile v1

Predicate identifier: `https://agenteval.dev/attestation/artifact-evidence/v1`

This separately versioned profile records a producer's assertion binding one explicitly identified,
immutable artifact to one sealed AEF run. It is outside AEF 1.0 and does not change the run schema.

The producer supplies the artifact digest. A subject reference, version label or filename does not
establish that digest. The binding identifies both the exact `seal.json` bytes and the AEF run hash;
verification must also check the underlying evidence and any required signer identity.

The normative mapping and compatibility rules are in the
[artifact-evidence profile](https://github.com/AgentEvalHQ/AgentEval/blob/main/contracts/aef/profiles/artifact-evidence/README.md).
The earlier draft identifier under `/aef/1/` was retired when the profile was separated from AEF 1.0;
it is not an alias for this identifier.

An intact binding is distinct from a trusted signature or deployment approval.
See the [AgentEval AEF guide](../../../aef.md) for companion export and verification commands.
