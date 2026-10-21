# Artifact evidence: binding an evaluated artifact to a sealed AEF run

Predicate type `https://agenteval.dev/attestation/artifact-evidence/v1`, document `schemaVersion` 1.0.

Status: a profile, outside AEF 1.0, with its own identifier and versions (GOVERNANCE.md, "Profiles"). The mapping,
the Results model and service, and the CLI are implemented in AgentEval and covered by companion and command
acceptance tests. It is not part of the AEF conformance corpus and no AEF conformance class requires it. It changes
nothing in the AEF 1.0 run schema.

## Purpose and boundary

An AEF seal identifies evidence files. It does not identify the evaluated software
artifact. This profile records a producer's assertion linking one immutable artifact
to one sealed AEF run. The producer supplies the artifact digest explicitly; neither
`subject.ref`, a version label, nor a filename is converted into a digest.

The mapping uses [in-toto Statement v1, pinned at v1.2.0](https://raw.githubusercontent.com/in-toto/attestation/v1.2.0/spec/v1/statement.md)
and the [DSSE envelope, pinned at v1.0.2](https://raw.githubusercontent.com/secure-systems-lab/dsse/v1.0.2/envelope.md).
The subject is matched by digest. DSSE verification must use the exact payload bytes
that the application subsequently consumes. These references describe the envelope;
the predicate semantics below belong to AgentEval.

## Document

The profile has exactly one subject and one evidence binding. All fields below are
required. Digest values are 64 lowercase hexadecimal characters. `runHash` uses the
existing AEF run-hash representation (64 lowercase hexadecimal characters) and is
checked by the existing run verifier. Authorized withholding retains the sealed
manifest identity; it does not imply that unavailable blob bytes were rehashed.

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [{
    "name": "example-agent.tar",
    "digest": {"sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
  }],
  "predicateType": "https://agenteval.dev/attestation/artifact-evidence/v1",
  "predicate": {
    "schemaVersion": "1.0",
    "binding": "producer-asserted",
    "evidence": {
      "seal": {"sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
      "runHash": "<the verified AEF run hash>"
    }
  }
}
```

The example digests and run hash are placeholders, not a valid fixture. The subject
name is a nonempty display label; it is never resolved as a local path or fetched.
The artifact digest covers the caller-selected immutable artifact bytes, not an
implicit directory traversal. A caller supplying an existing digest is asserting its
provenance; a byte-hashing helper can establish byte equality, not execution history.

The seal digest covers the exact `seal.json` bytes, including whitespace. `runHash`
is the logical AEF manifest identity. Both must match: reserializing a seal requires a
new companion even when its logical run hash is unchanged. A matching seal digest
alone does not establish that the underlying evidence files are intact.

The predicate URI names this profile; it is not a claim of external registration or
endorsement. Its major version is in the URI, as in-toto asks. Readers reject unsupported
predicate URIs, major schema versions, digest algorithms, binding kinds and subject
cardinalities. Later minor versions of major 1 are accepted when all required known
fields remain supported; malformed version strings are refused. Readers follow
AEF strict JSON rules (including duplicate-member rejection), preserve original
bytes for signature verification and ignore unknown optional members. Unknown
members never supply trust or change the interpretation of known fields.

## Storage and signing

Keep `artifact-evidence.json` and optional `artifact-evidence.dsse.json` outside the
sealed run directory. This avoids adding unlisted run files or a circular seal
dependency. Paths are caller-selected; names here are conventions. Writers must not
overwrite existing evidence or mutate the run.

The envelope's payload type is `application/vnd.in-toto+json`, and its payload is
the exact companion document. Reuse `DsseEnvelope.Create` and `DsseVerifier`; do not
add cryptography or JSON canonicalization. A signature over `seal.json` is not a
signature over the companion. The run's signer and the companion's signer may differ.
Unsigned companions are permitted and must be presented as unsigned.

## Verification and trust

Verification takes the companion, a caller-selected artifact SHA-256, the selected
run, an optional companion envelope, and optional caller-owned trust policy. Never
load keys or authority from companion metadata. Never fetch an artifact or evidence
location found in an untrusted document.

Report these separately, without converting them into an evaluation pass:

1. Document validity and supported profile.
2. Artifact digest match against the explicit caller selection.
3. Exact seal digest match and verified run-hash match.
4. Existing AEF run-verification outcome, including the withheld-file count;
   unsealed/invalid is not an intact binding. Withheld bytes remain unavailable,
   even when the existing verifier accepts their authorized withholding.
5. Companion signature identities verified under the supplied policy, or an explicit
   unsigned/not-checked result. A present malformed or mismatched envelope is reported.

An intact binding means these bytes refer to that evidence. A verified signature
identifies who asserted the link under the caller's policy. Neither proves that the
artifact was executed, that an evaluator was sound, or that a deployment is approved.
Authorization to accept a producer's assertion remains a consumer policy decision.

This profile binds base evidence only. It neither snapshots nor authenticates later
overlays, effective verdicts, waivers, checkpoints or governance reports. Those retain
their existing independent verification and time-selection rules.

## Implementation acceptance

- Keep models, parsing and verification in BCL-only Results; reuse existing seal,
  hash, JSON and DSSE primitives. Add interfaces for service boundaries only.
- Bound the companion document to 64 KiB and its optional envelope to 128 KiB before
  parsing or unbounded file reads. Limit the label to 1024 UTF-8 bytes. Honor
  cancellation and ordinary-file checks in file adapters; avoid implicit scanning.
- Read owned bytes once for hashing, parsing and signature verification. File-based
  run verification must use a stable capture or explicitly refuse concurrent changes;
  independently rereading paths must not be described as atomic verification.
- Test actual sealed fixtures: exact roundtrip; changed artifact, seal bytes and
  evidence file; wrong run hash; unsigned, trusted, untrusted and wrong-payload
  envelopes; duplicate JSON members; unsupported profile; exact size boundaries;
  cancellation and no run mutation. Verify all supported Results target frameworks.
- CLI output must distinguish producer assertion, byte binding and verified signer.
  Publishing predicates/media types and keyless Sigstore support remain separate.

The mapping, companion implementation and roundtrip acceptance are complete.
EvalPort conversion is described separately in [the EvalPort profile](../../1/interop/evalport.md).

## Results API

```csharp
IArtifactCompanionService companions = new ArtifactCompanionService();
// runDirectory is an explicit absolute local path; artifactSha256 is caller supplied.
var document = companions.Create(runDirectory, "agent.tar", artifactSha256);
var envelope = companions.Sign(document, signer);
var report = companions.Verify(document, artifactSha256, runDirectory, envelope, policy);
```

`Create` verifies a bounded owned copy and refuses unsealed or invalid evidence.
It returns a document without writing companion files into the run. `Sign` reuses
the existing DSSE signer and preserves document bytes. The caller owns the signer.
`Verify` returns `ArtifactMatches`, `SealMatches`, `RunHashMatches`, the existing
`Evidence` verification report, and separate `Signature` checks. `IntactBinding`
combines the byte-binding checks and base evidence integrity only; consumers must
also inspect the signature outcome and apply their own acceptance policy.

An absent envelope gives `Signature == null`. A present malformed envelope has an
explicit malformed result. Without caller policy, an empty trust policy is used and
`PolicySupplied` is false; no identity is trusted implicitly. Withholding remains
visible in `Evidence.Withheld`. The same caller policy is used for evidence redaction
authority and companion signer identification, without transferring authority
between them.

The shared internal capture reads an ordinary, bounded file inventory into a
temporary owned directory. Verification and seal hashing then use that copy; the
source is never modified. This is not an atomic filesystem snapshot of a changing
source. The captured evidence must verify as a whole, and observed size/type changes
are refused. Capture cancellation is cooperative; the synchronous run verifier is
checked for cancellation before and after its work, not forcibly interrupted.

## CLI

```sh
agenteval aef companion create ./run --artifact-sha256 <64-lowercase-hex> --name agent.tar --output ./companion --key ./producer.pem --json
agenteval aef companion verify ./run --artifact-sha256 <64-lowercase-hex> --document ./companion/artifact-evidence.json --envelope ./companion/artifact-evidence.dsse.json --policy ./trust.json --require-signer producer --json
```

Omit `--key` to create an unsigned companion. Creation publishes a new directory
containing the document and optional envelope by a same-parent directory rename.
The output must be outside the run, must not already exist, and must have an existing
ordinary parent directory. No existing output is overwritten. A failed signing step
publishes neither file. Private staging is cleaned up on handled failure; an abrupt
process termination can leave a `.ae-companion-*` directory in the selected parent.
This is local filesystem publication, not a cross-machine durability guarantee.

Verification reads only explicitly selected files; it does not discover envelopes
or policies beside the document. Bounds are 64 KiB for a document or key, 128 KiB
for an envelope and 1 MiB for a policy. Inputs and ancestors must be ordinary files
and directories, respectively. These checks do not promise protection against an
adversary concurrently replacing directories in a caller-controlled filesystem.

Exit 0 from verification means an intact byte binding, no detected envelope/signature
failure, and satisfaction of `--require-signer` if supplied. Without that option,
unsigned or untrusted bindings can return 0; this never means deployment approval.
Exit 1 means a binding, signature or required-identity check failed. Exit 2 is an
input/usage refusal. The JSON report always separates `intactBinding`,
`signatureStatus`, per-signature checks, verified identities and withheld count.
`--require-signer` requires an explicit policy and exact identity match.

## Changelog

- 2026-10-20: moved out of AEF 1.0's interop pages into this profile. The predicate type changed from
  `https://agenteval.dev/aef/1/artifact-evidence` to `https://agenteval.dev/attestation/artifact-evidence/v1`,
  because `/aef/1/` names only what AEF 1.0 itself defines. No released AgentEval version wrote the old type.
