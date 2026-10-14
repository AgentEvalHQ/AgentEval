# Artifact-bound evidence companion

Status: mapping accepted for implementation; writer/verifier acceptance is still pending.
This is an optional companion profile, not a change to the AEF 1.0 run schema.

## Purpose and boundary

An AEF seal identifies evidence files. It does not identify the evaluated software
artifact. This companion records a producer's assertion linking one immutable artifact
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
existing AEF run-hash representation and is recomputed by the existing run verifier.

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [{
    "name": "example-agent.tar",
    "digest": {"sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
  }],
  "predicateType": "https://agenteval.dev/aef/1/artifact-evidence",
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

The predicate URI is proposed for this profile; it is not a claim of external
registration or endorsement. Readers reject unsupported predicate URIs, major schema
versions, digest algorithms, binding kinds and subject cardinalities. Readers follow
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
3. Exact seal digest match and recomputed run-hash match.
4. Existing AEF run-verification outcome; unsealed/invalid is not an intact binding.
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

W09-01 completes this mapping only. W09-02 delivers implementation; W09-04 supplies
the broader interoperability roundtrip acceptance.
