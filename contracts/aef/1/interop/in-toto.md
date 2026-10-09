# in-toto, DSSE, SLSA and media types

**Versions read (2026-10-08):**

| Source | Version |
|---|---|
| in-toto Attestation Framework (`in-toto/attestation`) | main at `fd2609c16bcb0ac53443e2b4612977f997e8f9a5` (2026-09-14); the specification says v1.2 (release v1.2.0, 2026-03-18). Read: `spec/v1/statement.md`, `predicate.md`, `envelope.md`, `bundle.md`, `resource_descriptor.md`, `README.md` (parsing rules); `docs/new_predicate_guidelines.md`; `spec/predicates/README.md`, `test-result.md`, `svr.md`, `template/template.md` |
| DSSE (`secure-systems-lab/dsse`) | v1.0.2: `envelope.md`, `protocol.md` |
| SLSA | v1.2, Verification Summary (`https://slsa.dev/verification_summary/v1`) |
| Sigstore | `sigstore/protobuf-specs` `sigstore_bundle.proto`; `sigstore/cosign` `pkg/types/payload.go` |
| IANA | media types registry (last updated 2026-10-07); structured syntax suffix registry (last updated 2026-06-25); RFC 6838 |

This page also holds the specifications of AEF's two in-toto predicate types, in the shape the in-toto predicate
template (ITE-9) asks for: [AEF evidence](#predicate-type-aef-evidence) and
[AEF overlay batch](#predicate-type-aef-overlay-batch).

## The target

### Statements and predicates

An in-toto **Statement** v1 has `_type` `https://in-toto.io/Statement/v1`, a `subject` list, a `predicateType` (a URI)
and a `predicate`. Each subject must have a `digest`, and "subject artifacts are matched purely by digest". The
framework's parsing rules:

- consumers ignore fields they do not recognise;
- the type URI carries the major version, and a 0.x version counts as a major;
- extension field names avoid `.` and `$`;
- policies are **monotonic**: ignoring an attestation, or a field, never turns a DENY into an ALLOW.

### Listing a predicate type

Anyone may define a predicate type under a URI they control. Vetting by the in-toto maintainers is optional
(`predicate.md`: "New predicate types MAY be vetted"). The guidelines ask for:

- lowerCamelCase field names;
- RFC 3339 times in `Z`, named for what they mean (`builtAt` is better than a bare `timestamp`);
- the parsing rules followed, and the monotonic principle explained.

To be listed in the directory of vetted predicates, a predicate goes through this process:

1. A pull request with a document in the predicate template's shape, added to `spec/predicates/README.md`, optionally
   with a protobuf definition for language bindings.
2. Review at the next maintainers' meeting.
3. If accepted, a listing in the directory.
4. A redirect on `in-toto.io`, only for a type URI under `https://in-toto.io/attestation/`.

A proposal to split the directory into a contributed tier and a vetted tier with an evidence criterion (ITE-12,
`in-toto/ITE#63`) is open.

Vetted predicates close to evaluation results:

- **Test Result** (`https://in-toto.io/attestation/test-result/v0.1`): `result` (`PASSED`, `WARNED`, `FAILED`),
  `configuration` (resource descriptors), `url`, `passedTests`, `warnedTests`, `failedTests`.
- **Simple Verification Result** (`https://in-toto.io/attestation/svr/v0.2`): a `verifier` with its `policies`,
  `timeCreated`, and the `properties` it verified.

Open proposals for AI evaluation, none vetted:

- `eval-result`, issue #565 and PR #575: metric-threshold claims about a model, with salted commitments. It has no
  maintainer review and one implementation.
- PR #587: pre-registered evaluation criteria.
- Issue #554: agent decisions.
- PR #570: adversarial execution evidence.

### Envelopes and bundles

- **DSSE** (v1.0.2) signs `PAE(payloadType, payload)`, where
  `PAE = "DSSEv1" SP LEN(type) SP type SP LEN(body) SP body`. The envelope is
  `{payload, payloadType, signatures[{keyid, sig}]}`. DSSE asks for a `payloadType` that is an application-specific
  media type or a URI, never a generic one like `application/json`. DSSE defines no media type for the envelope
  itself.
- **in-toto envelopes**: "`payloadType` MUST be set to `application/vnd.in-toto.<predicate>+json` or to
  `application/vnd.in-toto+json`", where `<predicate>` must match the file name of a vetted predicate's specification.
  An envelope stored alone SHOULD end in `.json`. To name a single attestation in a store, in-toto uses
  `application/vnd.in-toto.<predicate>+dsse`.
- **in-toto bundles**: JSON Lines of envelopes, named `<file>.intoto.jsonl`, media type
  `application/vnd.in-toto.bundle`. A bundle is not authenticated as a whole.
- In practice, cosign uses `application/vnd.dsse.envelope.v1+json` for a DSSE envelope, and Sigstore bundles use
  `application/vnd.dev.sigstore.bundle.v0.3+json`.

### SLSA

SLSA defines its own predicates, Provenance and the Verification Summary Attestation (VSA:
`verifier.id`, `timeVerified`, `resourceUri`, `policy`, `inputAttestations`, `verificationResult` `PASSED` or
`FAILED`, `verifiedLevels`). It keeps no registry of predicate types. The in-toto directory is the only listing.

### Media type registration

None of `application/vnd.in-toto+json`, `application/vnd.in-toto.bundle`, the `+dsse` types or
`application/vnd.dsse.envelope.v1+json` is in the IANA media types registry. They are community conventions.

The registered structured syntax suffixes include `+json`, `+zip`, `+gzip`, `+zstd`, `+json-seq` and `+cbor`. There
is no `+jsonl`, `+ndjson` or `+dsse` suffix, and no `application/jsonl` or `application/x-ndjson` registration.
`+json-seq` (RFC 7464) separates records with an RS byte, so it does not describe NDJSON.

Under RFC 6838, the vendor tree (`vnd.<producer>.<name>`) is for "media types associated with publicly available
products", and its registrations "may be submitted directly to the IANA, where they will undergo Expert Review".

## How AEF uses them

| AEF | Role | Rule |
|---|---|---|
| `seal.json` | an in-toto Statement v1, predicate type `https://agenteval.dev/aef/1/evidence` | [SEAL-5](../spec/04-integrity.md#41-sealing-a-run) |
| `overlays/seal-<nnnn>.json` | an in-toto Statement v1, predicate type `https://agenteval.dev/aef/1/overlay-batch` | [OVL-4](../spec/04-integrity.md#42-overlays) |
| `attestation.dsse.json` | a DSSE envelope over the exact bytes of `seal.json`, `payloadType` `application/vnd.in-toto+json` | [SIG-1](../spec/04-integrity.md#44-signatures) |
| `overlays/seal-<nnnn>.dsse.json` | a DSSE envelope over a batch seal, `payloadType` `application/vnd.in-toto+json` | SIG-1 |
| `<checkpoint>.dsse.json` | a DSSE envelope over a checkpoint manifest, which is not an in-toto Statement; `payloadType` `application/vnd.agenteval.aef.checkpoint+json` | SIG-1, [CKP-5](../spec/05-checkpoints.md#51-the-manifest) |
| key ids, algorithms | `keyid` is `sha256:` of the public key's SubjectPublicKeyInfo; ECDSA P-256 or Ed25519 | [SIG-2, SIG-3](../spec/04-integrity.md#44-signatures) |
| Sigstore | not in AEF 1.0's trust policy: a later minor may add keyless identities, and a 1.0 verifier refuses a policy that uses them | [SIG-4](../spec/04-integrity.md#44-signatures) |

Both statements follow the in-toto conventions: a self-hosted type URI that carries the major version,
lowerCamelCase fields, RFC 3339 times in `Z`, a `sha256` digest per subject. Both envelopes follow DSSE and the
in-toto envelope rules: the generic in-toto payload type, and file names that end in `.json`.
`application/vnd.in-toto.aef-evidence+json` would only become correct if the predicate were vetted under a file named
`aef-evidence.md`.

## AEF → in-toto and SLSA

| AEF | in-toto / SLSA | Fidelity |
|---|---|---|
| `seal.json` | an in-toto Statement, as it is | exact |
| `attestation.dsse.json` | a DSSE envelope, verifiable by any DSSE implementation that supports the key | exact |
| overlay batch seals and their envelopes | in-toto Statements and DSSE envelopes | exact; a generic verifier must know that the subject digest covers a byte range (see the [overlay batch](#predicate-type-aef-overlay-batch) parsing rules) |
| root result lines | a Test Result statement: `passed` → `passedTests`, `warn` → `warnedTests`, `failed` → `failedTests`, by `caseId` | lossy: `inconclusive`, `scored` and the typed absences have no list, and scores, paths and the tree are lost |
| a gate decision's `outcome` ([GATE-1](../spec/03-run.md#38-gatesndjson)) | Test Result `result`: `ship` → `PASSED`, `no_ship` → `FAILED`; `inconclusive` has no value | lossy |
| `run.json`, `suite.digest` | Test Result `configuration` (resource descriptors with `sha256`) | exact |
| a decided checkpoint ([§5](../spec/05-checkpoints.md#51-the-manifest)) | an SVR: `verifier.id`, `timeCreated` from `decisionInput.evaluatedAt`, `policies` = the manifest as a resource descriptor, `properties` such as `AEF_CHECKPOINT_APPROVED` | lossy: only the approved properties are listed, which keeps the policy monotonic; `approved_with_exceptions` gets its own property (`AEF_CHECKPOINT_APPROVED_WITH_EXCEPTIONS`), never the plain approved one |
| a decided checkpoint | a SLSA VSA: `resourceUri` from `subject.ref` and `version`, `verificationResult` `PASSED` for `approved`, else `FAILED` (`approved_with_exceptions` included: a VSA has no way to say "passed, with accepted risks") | lossy: `verifiedLevels` expects SLSA levels, and an outcome is not one |
| (the evaluated artifact's digest) | the `subject` of any of these statements | none: `run.json` names the subject by `ref` and `version`; the digest of an image appears only in a run plan's `subject.image` ([I8](README.md#gaps-found-by-these-mappings)) |

## in-toto → AEF

| in-toto | AEF | Fidelity |
|---|---|---|
| another signer's DSSE envelope over the same `seal.json` bytes | another signature in `attestation.dsse.json`, or a second envelope kept beside the run | exact; a verifier checks each signature against its trust policy ([SIG-5](../spec/04-integrity.md#44-signatures)) |
| a Sigstore bundle | none in 1.0: keyless signing is not part of the trust policy ([SIG-4](../spec/04-integrity.md#44-signatures)) | lost: a 1.0 verifier needs a public key in its policy |
| a Test Result statement | a run with one root line per listed test: `passed`, `warn` or `failed` | lossy: no scores. The converter supplies the subject, suite and target mode and lists them in `imported.asserted` ([RUN-15](../spec/03-run.md#32-runjson)) |
| a Test Result's `url` | an evidence record with a URI link | exact |

## What does not carry over

**AEF → in-toto.** Everything in the run's files beyond their digests: in-toto attests the files, and does not read
them. In a Test Result: `inconclusive`, `scored` and typed absences, scores, paths, the tree, severity. A link from the evidence
to the evaluated artifact by digest ([I8](README.md#gaps-found-by-these-mappings)).

**in-toto → AEF.** In a Test Result: scores and the reasons for each test's outcome. Attestations about other
artifacts in the same bundle (provenance, SBOMs), which AEF does not hold.

## Worked example

The corpus line for `case-18`. Its `resultId` recomputes with [RES-4](../spec/03-run.md#342-result-ids):

```json
{"schemaVersion":"1.0","resultId":"r_3b156bcb57417545db27c39e78c955bd","parentResultId":null,"caseId":"case-18","path":"triage","evaluator":{"id":"composite:triage","version":"2"},"state":"warn","severity":"low","scores":[{"metric":"triage","value":0.78,"normalized":0.78}]}
```

In a Test Result statement for the whole run, `case-18` is listed in `warnedTests`:

- `case-17`, the only root line in `failed`, is in `failedTests`.
- The other roots have no list: `case-19` (`inconclusive`), `case-20` (`not_measured`), `case-21` (`skipped`) and
  `case-22` (`error`).
- `result` is `FAILED`, from the gate decision `no_ship`.
- The configuration names `run.json` and the suite by their SHA-256.

The subject is `seal.json` (2336 bytes, SHA-256 below). `run.json` names the evaluated agent only as
`agent:support/support-triage` at `git:3f2a1c`, which is no digest ([I8](README.md#gaps-found-by-these-mappings)).

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [
    {
      "name": "seal.json",
      "digest": {
        "sha256": "8dfc2032726fddf7b420ef4ecf4649bbe9460b675a158805247b1f30620bea84"
      }
    }
  ],
  "predicateType": "https://in-toto.io/attestation/test-result/v0.1",
  "predicate": {
    "result": "FAILED",
    "configuration": [
      {
        "name": "run.json",
        "digest": {
          "sha256": "6833f5f2e31f266973749d72f60cf9656d8689145780a14670e35c07795c1f5c"
        }
      },
      {
        "name": "suite:support/triage-scenarios@4",
        "digest": {
          "sha256": "f1b4f6f2c254e91b0c23b6c40c1554dd8a7b04715858206d4537698e34caaedd"
        }
      }
    ],
    "passedTests": [],
    "warnedTests": [
      "case-18"
    ],
    "failedTests": [
      "case-17"
    ]
  }
}
```

Read back, the statement gives root lines for `case-17` (`failed`) and `case-18` (`warn`) at a path the importer has to
choose. With the path `triage` and the original run id, RES-4 gives the original id for `case-18`,
`r_3b156bcb57417545db27c39e78c955bd`. The score 0.78 and the four other cases are lost.

## Predicate type: AEF evidence

Type URI: `https://agenteval.dev/aef/1/evidence`

Version: 1.0

Predicate Name: aef-evidence

### Purpose

Seal one AEF run: list every file of a closed run folder with the SHA-256 of its exact bytes, and bind them to the
run's identity, its run hash and the facts of its header. A verifier can then show that the files are the ones that
were sealed ([§4.1](../spec/04-integrity.md#41-sealing-a-run)). A DSSE signature over the statement shows who sealed
them ([§4.4](../spec/04-integrity.md#44-signatures)).

### Use Cases

- A producer seals the evaluation run it just closed. A release reviewer later checks that no file changed
  ([SEAL-6](../spec/04-integrity.md#41-sealing-a-run)).
- A release checkpoint names the runs it relied on by run hash ([CKP-2](../spec/05-checkpoints.md#51-the-manifest)).
  The statement is how a verifier recomputes that hash and finds the exact run
  ([CKP-8](../spec/05-checkpoints.md#55-verifying-a-checkpoint)).
- A host takes custody of an unsealed run and seals it (`sealedBy: ingest`).

The vetted predicates do not cover this:

- Test Result lists test names by outcome.
- Simple Verification Result lists verified properties.
- SLSA Provenance describes how an artifact was built.
- Release and Reference point at artifacts.

None of them lists the files of an evaluation run and binds them to the run's identity, its subject, suite and judges.

### Prerequisites

The in-toto Attestation Framework v1 (Statement v1). The AEF 1.0 specification, sections 2 to 4: encoding, the run
folder, integrity. DSSE v1 for signatures.

### Model

- **Step:** closing an evaluation run.
- **Functionary:** the producer that ran the evaluation (`sealedBy: producer`), or a host that took custody of the run
  (`sealedBy: ingest`).
- **Subjects:** the run's sealed files: every file of the folder except `seal.json`, `attestation.dsse.json` and
  `overlays/` ([SEAL-1](../spec/04-integrity.md#41-sealing-a-run)).
- **Consumers:** run verifiers, checkpoint verifiers, and release policies that rely on them.

### Schema

```jsonc
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [{ "name": "<path in the run folder>", "digest": { "sha256": "<hex>" } }, ...],
  "predicateType": "https://agenteval.dev/aef/1/evidence",
  "predicate": {
    "schemaVersion": "1.0",
    "runId": "<id>",
    "runHash": "<hex>",
    "producer": { "name": "<string>", "version": "<string>" },
    "subject": { "ref": "<kind:name>", "version": "<exact version>" },
    "deployment": { "ref": "<kind:name>" } | null,
    "suite": { "ref": "<kind:name>", "version": "<exact version>", "digest": "sha256:<hex>" } | null,
    "judges": [{ "model": "<string>", "rubricDigest": "sha256:<hex>" }, ...],
    "closedAt": "<RFC 3339, Z>",
    "sealedAt": "<RFC 3339, Z>",
    "sealedBy": "producer" | "ingest"
  }
}
```

The normative schema is [`schemas/writer/seal.schema.json`](../schemas/writer/seal.schema.json).

#### Parsing Rules

The framework's standard parsing rules apply, with these differences and additions:

1. **The subject list is the whole run.** There is one entry per sealed file. `name` is the file's path in the run
   folder ([RUN-3](../spec/03-run.md#31-a-run-is-one-folder)), and `digest` holds `sha256` only. Consumers match
   subjects by name **and** digest. A file present in the folder with no subject is a problem (`not-sealed`). A subject
   with no file is `missing`, or `withheld` when a verified overlay withheld it
   ([SEAL-6](../spec/04-integrity.md#41-sealing-a-run)).
2. **The run hash is recomputed.** `runHash` is the SHA-256 of the manifest of the subjects
   ([SEAL-3, SEAL-4](../spec/04-integrity.md#41-sealing-a-run)). A consumer treats it as verified only after
   recomputing it from the files.
3. **The predicate restates `run.json`.** A verifier compares the producer, subject, deployment, suite, judges and
   `closedAt` with `run.json` (`predicate` problem, SEAL-6).
4. **Exact bytes.** The statement is read and signed as the bytes of `seal.json`. There is no canonical form. It is an
   I-JSON document ([ENC-2](../spec/02-encoding.md#21-json-documents)).
5. **Versions.** The type URI carries the major version (`/aef/1/`). `predicate.schemaVersion` carries `MAJOR.MINOR`.
   A minor version only adds optional fields and enum values ([VER-5](../spec/07-versioning.md#72-what-a-minor-version-may-change)). A reader ignores
   fields it does not know and reads an unknown `sealedBy` as `ingest`
   ([§7.3](../spec/07-versioning.md#73-reading-a-value-this-version-does-not-know)). A writer writes only the fields of
   its version.
6. **Monotonic.** A run without a seal that verifies is `unsealed` or `invalid`, and neither counts as verified
   ([§4.5](../spec/04-integrity.md#45-verification-outcomes)). Ignoring this statement can only lower what a policy
   concludes. A seal shows that the files are unchanged since sealing. It does not show who sealed them, which needs
   a signature, nor that the results are true ([SIG-7](../spec/04-integrity.md#45-verification-outcomes)).

#### Fields

Statement layer:

| Field | Type | Required | Description |
|---|---|---|---|
| `subject` | array of 1 to 100,000 objects | yes | One per sealed file. No entry names `seal.json`, `attestation.dsse.json` or a file under `overlays/` (`subject-path`). No name appears twice (`duplicate-subject`). |
| `subject[].name` | string | yes | The file's path in the run folder: ASCII letters, digits, `.`, `_`, `-`, segments joined by `/`, at most 255 bytes |
| `subject[].digest.sha256` | string | yes | 64 lower-case hex characters: the SHA-256 of the file's exact bytes |

Predicate:

| Field | Type | Required | Description |
|---|---|---|---|
| `schemaVersion` | string | yes | `1.0` for this version |
| `runId` | string | yes | `run.json`'s `runId` |
| `runHash` | string, 64 hex | yes | The SHA-256 of the manifest (SEAL-3, SEAL-4) |
| `producer` | object: `name`, `version` | yes | As `run.json` gives them |
| `subject` | object: `ref`, optional `version` | yes | What was evaluated, as `run.json` gives it |
| `deployment` | object with `ref`, or `null` | yes | Where it ran; `null` when `run.json` has no `deployment` |
| `suite` | object: `ref`, `version`, optional `digest` (`sha256:<hex>`), or `null` | yes | The cases that ran; `null` when `run.json` has no `suite` |
| `judges` | array of objects: `model`, optional `rubricDigest` | yes | The judges, in `run.json` order; `[]` when `run.json` has none |
| `closedAt` | RFC 3339 time in `Z` | yes | `run.json`'s `endedAt` |
| `sealedAt` | RFC 3339 time in `Z` | yes | When the seal was made |
| `sealedBy` | `producer` or `ingest` | yes | Who sealed: the producer, or a host on taking custody |

### Example

The seal of the corpus run [`conformance/valid/completed-eval/run/`](../conformance/valid/completed-eval/run/), with
one subject per line:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [
    {"name": "blobs/sha256/63/635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5", "digest": {"sha256": "635221c9c64f48e2843e4186b0a1b66f07a1492c14dcb866bb83dba6a5e5fef5"}},
    {"name": "evidence.ndjson", "digest": {"sha256": "f76af41ebe790477719e419c60ab430ef5cc98263b8fd8dd5bb52988a03108b1"}},
    {"name": "gates.ndjson", "digest": {"sha256": "6db8527cfa0e3d60c242253e40f123eb56b4105f2b14a622af21370fd10b9c63"}},
    {"name": "metrics.json", "digest": {"sha256": "cd5980b59a1023260f2c7fa19d0a8075802dbb17e83485a547dc3b977649ef87"}},
    {"name": "results.ndjson", "digest": {"sha256": "08719682aba1da613d9712bdc1ad15c838048a064843fe12f61a36f7b6936e5c"}},
    {"name": "run.json", "digest": {"sha256": "6833f5f2e31f266973749d72f60cf9656d8689145780a14670e35c07795c1f5c"}},
    {"name": "summary.json", "digest": {"sha256": "268eda205f131668d931d8804c9530cb7c99b2857d95e1ce1bc24fc3d6314679"}},
    {"name": "traces.otlp.jsonl", "digest": {"sha256": "3c29fa780cb029dd541c36edb35614f49df76f459a1f7c3e3702960892398787"}}
  ],
  "predicateType": "https://agenteval.dev/aef/1/evidence",
  "predicate": {
    "schemaVersion": "1.0",
    "runId": "01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10",
    "runHash": "f16b6a74505a359810e44611732c4d53ff61d32852983b165414811eb6a6627b",
    "producer": {
      "name": "agenteval-cli",
      "version": "1.0.0"
    },
    "subject": {
      "ref": "agent:support/support-triage",
      "version": "git:3f2a1c"
    },
    "deployment": {
      "ref": "deployment:support/support-triage@dev"
    },
    "suite": {
      "ref": "suite:support/triage-scenarios",
      "version": "4",
      "digest": "sha256:f1b4f6f2c254e91b0c23b6c40c1554dd8a7b04715858206d4537698e34caaedd"
    },
    "judges": [
      {
        "model": "gpt-5.1",
        "rubricDigest": "sha256:29fd018a9848938bc2b0e33fffa32bde2827e81388d0e03195919be5835c3605"
      }
    ],
    "closedAt": "2026-10-02T14:06:23.004Z",
    "sealedAt": "2026-10-02T14:06:24Z",
    "sealedBy": "producer"
  }
}
```

The corpus file holds the same JSON value with one member per line. A signature covers the file's exact bytes.

### Changelog and Migrations

1.0: first version. Later minor versions are listed in [`CHANGELOG.md`](../CHANGELOG.md).

## Predicate type: AEF overlay batch

Type URI: `https://agenteval.dev/aef/1/overlay-batch`

Version: 1.0

Predicate Name: aef-overlay-batch

### Purpose

Seal one batch of events appended to a closed run after it closed: approvals, rejections, waivers, adjudications,
notes, redactions ([§4.2](../spec/04-integrity.md#42-overlays)). Each batch statement names the previous one, so the
batches form a chain over the events file. Each names the run hash of the run it was appended to.

### Use Cases

- A reviewer approves a run, or waives a requirement until a date. The batch that holds the event is sealed, and can
  be signed, so a reader can tell who made the decision and that it was not changed later
  ([OVL-3](../spec/04-integrity.md#42-overlays)).
- A reader checks the chain from the first batch to the last and reports changed, missing or uncovered bytes
  ([OVL-5](../spec/04-integrity.md#42-overlays)).
- A blob holding personal data is withheld by a sealed `redact` event, and the run's own seal still verifies
  ([OVL-10](../spec/04-integrity.md#43-the-effective-view)).

The events file is append-only, so the whole file has no stable digest. A statement over one byte range at a time
lets earlier batches stay sealed while later ones are added. No vetted predicate covers this.

### Prerequisites

The in-toto Attestation Framework v1. AEF 1.0, §4.2 to §4.4. The run's own seal ([AEF evidence](#predicate-type-aef-evidence)).

### Model

- **Step:** appending events to a closed run.
- **Functionary:** whoever appends the batch: a reviewer's tool, or a host.
- **Subject:** one, the events file `overlays/events.ndjson`, with the digest of the batch's bytes.

### Schema

```jsonc
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [{ "name": "overlays/events.ndjson", "digest": { "sha256": "<hex of the batch's bytes>" } }],
  "predicateType": "https://agenteval.dev/aef/1/overlay-batch",
  "predicate": {
    "schemaVersion": "1.0",
    "runId": "<id>",
    "runHash": "<hex>",
    "batch": <integer, 1-based>,
    "offset": <integer>,
    "length": <integer>,
    "previous": { "path": "overlays/seal-<nnnn>.json", "sha256": "<hex>" } | null
  }
}
```

The normative schema is [`schemas/writer/overlay-seal.schema.json`](../schemas/writer/overlay-seal.schema.json). The
statement of batch *n* is the file `overlays/seal-<nnnn>.json`, with *n* in four digits.

#### Parsing Rules

The framework's standard parsing rules apply, with these differences and additions:

1. **The subject digest covers a byte range.** It is the SHA-256 of the bytes from `offset` to `offset` + `length` of
   `overlays/events.ndjson`, whole lines only. A consumer **MUST NOT** compare it with the digest of the whole file.
   This departs from the framework's model, in which a subject digest identifies the whole artifact.
2. **The batches chain.** Batch 1 has `previous: null`. Batch *n* names the file of batch *n* − 1 and the SHA-256 of
   its bytes. Each batch's `offset` continues the previous batch. The batches cover the file without gaps
   ([OVL-4](../spec/04-integrity.md#42-overlays)).
3. **The run.** `runId` and `runHash` are those of the run the batch was appended to. A batch for another run, or for
   another run hash, is a problem (`run-id`, `run-hash`; [OVL-5](../spec/04-integrity.md#42-overlays)).
4. **Monotonic.** Events after the last batch that verifies have no effect
   ([§4.3](../spec/04-integrity.md#43-the-effective-view)). Ignoring a batch statement can only remove decisions from
   the effective view. The chain cannot show that the newest batches were removed together with their statements; a
   signed newest batch or a copy held elsewhere can.
5. **Versions and bytes** as for [AEF evidence](#parsing-rules), rules 4 and 5.

#### Fields

| Field | Type | Required | Description |
|---|---|---|---|
| `subject` | array of exactly 1 object | yes | `name` is `overlays/events.ndjson`; `digest.sha256` is the SHA-256 of the batch's bytes |
| `schemaVersion` | string | yes | `1.0` for this version |
| `runId` | string | yes | The run's `runId` |
| `runHash` | string, 64 hex | yes | The run hash of the run the batch was appended to |
| `batch` | integer ≥ 1 | yes | The batch number; equal to the number in the file name |
| `offset` | integer ≥ 0 | yes | Where the batch starts in the events file, in bytes |
| `length` | integer ≥ 1 | yes | The batch's length in bytes: whole lines, each ending in LF |
| `previous` | object (`path`, `sha256`) or `null` | yes | The previous batch's statement file and the SHA-256 of its bytes; `null` for batch 1 |

### Example

The second batch of the corpus run, `overlays/seal-0002.json`, as written:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [
    {
      "name": "overlays/events.ndjson",
      "digest": {
        "sha256": "491a42d4ce4375c72fdab49fe2b4d9802909edbdd43f673618fade23a181ae9f"
      }
    }
  ],
  "predicateType": "https://agenteval.dev/aef/1/overlay-batch",
  "predicate": {
    "schemaVersion": "1.0",
    "runId": "01928f3e-7c1a-7b2e-9a51-3f2c0d4e8a10",
    "runHash": "f16b6a74505a359810e44611732c4d53ff61d32852983b165414811eb6a6627b",
    "batch": 2,
    "offset": 343,
    "length": 316,
    "previous": {
      "path": "overlays/seal-0001.json",
      "sha256": "8f80b91ba250eacf6c8274d2882871784063f16b41398e08bc72892641d3cb15"
    }
  }
}
```

### Changelog and Migrations

1.0: first version. Later minor versions are listed in [`CHANGELOG.md`](../CHANGELOG.md).

## Media types

**Proposed. None of these is registered with IANA.** They follow the vendor-tree form of RFC 6838
(`vnd.agenteval.aef.*`), and become usable names only after an IANA registration, which is subject to Expert Review
([I8](README.md#gaps-found-by-these-mappings)). One is already used by the specification: the checkpoint payload type of SIG-1.

| Artifact | Proposed media type | Note |
|---|---|---|
| `run.json` | `application/vnd.agenteval.aef.run+json` | `+json` is registered |
| `metrics.json` | `application/vnd.agenteval.aef.metrics+json` | |
| `summary.json` | `application/vnd.agenteval.aef.summary+json` | |
| `results.ndjson`, `evidence.ndjson`, `gates.ndjson`, `overlays/events.ndjson` | `application/vnd.agenteval.aef.results`, `…aef.evidence`, `…aef.gates`, `…aef.overlay-events`, each defined in its registration as NDJSON ([§2.2](../spec/02-encoding.md#22-ndjson-files)) | no NDJSON suffix is registered; `+json-seq` is a different framing |
| a checkpoint manifest | `application/vnd.agenteval.aef.checkpoint+json` | already the DSSE `payloadType` of SIG-1 |
| `seal.json`, `overlays/seal-<nnnn>.json` | `application/vnd.in-toto+json` | the in-toto convention for a Statement (unregistered) |
| `attestation.dsse.json`, `overlays/seal-<nnnn>.dsse.json` | `application/vnd.dsse.envelope.v1+json` | the convention cosign uses (unregistered); in-toto's `application/vnd.in-toto.<predicate>+dsse` needs a vetted predicate file name |
| a run folder packed into one file | `application/vnd.agenteval.aef.run+zip`, file extension `.aef.zip` | `+zip` is registered; see the packing note below |
| envelopes collected for a release | `<artifact>.intoto.jsonl`, `application/vnd.in-toto.bundle` | the in-toto bundle convention (unregistered) |

**Packing a run into one file.** The seal covers the run's files and nothing around them, so a ZIP of a run is a way
to move it, and is no evidence by itself. A packed run that unpacks into a valid run keeps these properties:

- entry names are exactly the run's paths ([RUN-3](../spec/03-run.md#31-a-run-is-one-folder));
- each name appears once; there are no links and no extra entries, such as `__MACOSX/`, since an extra file is
  `not-sealed` ([RUN-2](../spec/03-run.md#31-a-run-is-one-folder));
- entries are stored or Deflate-compressed, which every ZIP reader supports;
- ZIP metadata (times, order, comments) is outside the seal.

The package's own digest is not the run hash. A reader verifies the files after unpacking, or streams them into the
manifest ([SEAL-3](../spec/04-integrity.md#41-sealing-a-run)).

## Still open

- [I8](README.md#gaps-found-by-these-mappings): no attestation names the evaluated artifact by digest, so in-toto and SLSA policies
  keyed on an artifact do not find AEF evidence; the media types above are unregistered.
