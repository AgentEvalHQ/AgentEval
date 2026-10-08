# 4. Integrity

AEF protects evidence in three layers, each stronger than the last (§4.5): a **seal** shows a run's files are
unchanged since it was sealed; a **signature** shows who sealed it; an **anchor** shows it is the run that was relied
on. §8 says what each layer does and does not protect against.

## 4.1 Sealing a run

A run is sealed over its bytes. There is no canonical JSON: nothing is re-encoded, and the corpus includes non-ASCII
text and a CRLF inside a sealed blob to prove it.

- **[SEAL-1] The sealed files** are every file in the run folder except `seal.json`, `attestation.dsse.json` and
  everything under `overlays/`. Only a closed run is sealed. A host that seals a run on taking custody of it
  (`sealedBy: ingest`) seals only a run whose files are valid against the reader schemas.
- **[SEAL-2]** Each file's digest is the SHA-256 of its exact bytes.
- **[SEAL-3] The manifest** has one line per sealed file, ordered by the UTF-8 bytes of its path (`/` separators; so
  `ext/Z` before `ext/a-b` before `ext/a.b` before `ext/a/b`):

  ```
  <sha256-hex>␠␠<size in bytes, decimal>␠␠<path>\n
  ```

  (Like `sha256sum` output with a size column; `sha256sum -c` cannot read it, by design.)
- **[SEAL-4] The run hash** is the SHA-256 of the manifest's bytes. It identifies the run's exact content: two runs
  with the same run hash have the same sealed files. Everywhere this specification says **a run's run hash**, it
  means: for a run with a `seal.json` valid against the reader seal schema, its `predicate.runHash`; for a run
  without one, the run hash recomputed from its files. (The seal check, §4.1, is what ties the sealed value to the
  files; with a withheld blob, only the sealed value can be known.)
- **[SEAL-5] `seal.json`** is an in-toto Statement v1: `_type` `https://in-toto.io/Statement/v1`; one `subject` per
  sealed file (`name` its path, `digest.sha256` its digest); `predicateType` `https://agenteval.dev/aef/1/evidence`;
  and a `predicate` with the `runId`, the `runHash`, the producer, subject, deployment, suite and judges as `run.json`
  gives them, `closedAt` (`run.json`'s `endedAt`), `sealedAt` (when the seal was made), and `sealedBy`: `producer`
  when the producer sealed the run, `ingest` when a host did. Where `run.json` has no `deployment` or no `suite`, the
  predicate's is `null`; with no `judges`, it is `[]`. For the `predicate` check, each of these equals the field's
  absence.
- **[SEAL-6] Verifying a seal** reports every difference as a path and a code, ordered as §3.9 orders problems. Verification stops after `seal-invalid`.

  | Code | Path | When |
  |---|---|---|
  | `seal-invalid` | `seal.json` | it is not valid against the reader seal schema, or not an I-JSON document |
  | `duplicate-subject` | the subject's name | a subject listed more than once (its digests are not compared) |
  | `subject-path` | the subject's name | a subject that names `seal.json`, `attestation.dsse.json` or a file under `overlays/` |
  | `digest` | the file | a sealed file whose bytes changed |
  | `not-sealed` | the file | a file present but not sealed |
  | `missing` | the file | a sealed file that is gone, and no authorized redaction withholds it ([OVL-10]) |
  | `withheld` | the file | a sealed blob that is gone, withheld by an authorized redaction ([OVL-10]) |
  | `run-hash` | `seal.json` | every file matches its subject, but the recomputed run hash is not `predicate.runHash` |
  | `run-id` | `seal.json` | `predicate.runId` is not `run.json`'s |
  | `predicate` | `seal.json` | the predicate differs from `run.json` (times compared as times, [ENC-8]: `…:02Z` equals `…:02.000Z`) about the producer (name, version), subject (ref, version), deployment (ref), suite (ref, version, digest), judges (model, rubric digest, in order), or `closedAt` |
  | `run-open` | `run.json` | `run.json` says `running` |

  A seal **verifies** when there are no problems other than `withheld`. A run with no `seal.json` is **unsealed**:
  neither verified nor failed.

## 4.2 Overlays

What is added to a run after it closed: approvals, rejections, waivers, adjudications, notes, redactions.

- **[OVL-1]** `overlays/events.ndjson` is append-only: a writer only ever appends whole lines (§2.2). Each event has a
  unique `eventId`, a `kind`, a `target`, who (`by`) and when (`at`).

  | `kind` | Target | Effect in the effective view (§4.3) |
  |---|---|---|
  | `approve`, `reject` | the run, or a result | the target's review status |
  | `override` | a result, with the `state` it sets and a `reason` | the result's effective state |
  | `adjudicate` | a result, with the `state` it sets and a `reason` | the result's effective state (resolving a split) |
  | `waive` | a result or a requirement, with a `reason` and an `expires` time | waived until `expires` |
  | `acknowledge`, `accept_baseline`, `annotate` | the run, a result or a requirement | recorded, no effect on states |
  | `redact` | a blob of the run (its SHA-256), with a `reason` | the blob is withheld (§4.3) |

- **[OVL-2]** An event's `target.run` is the run's own `runId`, and its `target.runHash`, when present, is the run's
  run hash. An overlay never targets anything outside its run.
- **[OVL-3]** `by.assurance` is what the writer **claims**: `self-attested`, `signed` or `authenticated`. Anyone who can
  append to the file can write any of them. A reader shows `signed` only when the batch holding the event has a
  signature that verifies against its trust policy (§4.4) for that identity, `authenticated` only for an event it
  received from a host it trusts, and otherwise `self-attested`, saying the claim was not verified.
- **[OVL-4] Batches.** Events are sealed in batches. Batch *n* is the bytes of the events appended since batch *n*−1:
  whole lines, at `offset` with `length`. `overlays/seal-<nnnn>.json` (1-based, four digits) is an in-toto Statement v1
  with `predicateType` `https://agenteval.dev/aef/1/overlay-batch`, whose subject is `overlays/events.ndjson` with the
  SHA-256 of the batch's bytes, and whose predicate holds `batch` (its number), the `runId`, the **`runHash` of the run
  it was appended to**, `offset`, `length`, and `previous` (the previous seal file's name and the SHA-256 of its bytes;
  `null` for batch 1). The batches cover the events file from its first byte to its last without gaps.
- **[OVL-5] Verifying the chain.** A verifier checks the seals from `seal-0001.json` to the highest-numbered one present
  and reports, per path, in the order of §3.9. "The run's run hash" is the `runHash` of the run's `seal.json` when it
  is valid against the reader seal schema, and otherwise the run hash recomputed from the files ([SEAL-4]): with a
  withheld blob, only the sealed value can be known. An events file whose framing breaks [ENC-5] or [ENC-7] (a CR, a
  blank line, a missing final LF) is reported once as `encoding` at `overlays/events.ndjson`, and the chain is not
  checked further: no batch of it verifies, and no event of it takes part in the effective view. A line reported as
  `event-invalid` is not checked for `event-id`
  or `target`, and its id is not recorded. A batch whose range runs past the end of the file is reported as both
  `line-boundary` and `batch-digest`.

  | Code | When |
  |---|---|
  | `batch-invalid` | a batch seal that is not valid against the reader schema (that seal is not checked further) |
  | `missing` | a seal missing below the highest present |
  | `batch-number` | a predicate `batch` that is not the file's number |
  | `run-id` | another `runId` |
  | `run-hash` | a `runHash` that is not the run's run hash |
  | `offset` | an `offset` that does not continue the previous batch (not checked after a missing seal) |
  | `line-boundary` | a range that does not start and end on a line boundary inside the file |
  | `batch-digest` | bytes that no longer match the batch digest |
  | `previous` | a `previous` that does not name the previous seal file and the SHA-256 of its bytes, including when that file is missing |
  | `uncovered` | for `overlays/events.ndjson`: bytes no batch claims (a claimed range counts as covered even when its bytes changed, which `batch-digest` reports) |
  | `event-invalid` | for `overlays/events.ndjson:<line>`: a line that is not an I-JSON object valid against the reader `overlay-event` schema; the event takes no part in the effective view |
  | `event-id` | for `overlays/events.ndjson:<line>`: an `eventId` an earlier line already has |
  | `target` | for `overlays/events.ndjson:<line>`: an event whose `target.run` is not the run's `runId`, whose `target.runHash` is not its run hash, or whose `target.result` is no result of the run ([OVL-2]) |
  | `unexpected-file` | a file under `overlays/` that is not the events file, a batch seal or a batch signature |

  Only files named `seal-`, four digits and `.json` are batch seals; `seal-0000.json` is reported as `batch-number`
  and not checked further. The run's own seal is unaffected by any of these, and so is the run's verification
  outcome (§4.5): an overlay verifier reports them.
- **What the chain cannot show:** removing the newest batches together with their seals leaves a shorter chain that
  verifies. A reader that has seen a longer chain keeps its length; a signed newest batch, or a copy held elsewhere
  (an anchor, §4.5), detects it.

## 4.3 The effective view

An overlay never changes a sealed file. A reader that shows a run with its overlays shows the **effective view**,
computed from the events of the **verified batches**: batch 1 and each following batch, up to the first batch with a
problem of [OVL-5] about the batch itself (any code but `event-invalid`, `event-id` and `target`, which concern
single events): that batch and every later one have no effect, even if they verify on their own. Within
them, an event reported as `event-invalid`, as `target`, or as `event-id` (the later of two events with one id) has
no effect either. Events after the last verified batch are shown as unsealed and have no effect:

- **[OVL-6]** Events apply in file order. `at` is shown, never used to reorder.
- **[OVL-7]** A result's effective state is the `state` of the last `override` or `adjudicate` targeting it, or its
  sealed state when there is none. The reader shows both. Parents, summaries, gate decisions and lane evaluation
  (§5.3) are **not** recomputed: they describe the sealed run.
- **[OVL-8]** A target's review status is the kind of the last `approve` or `reject` targeting it.
- **[OVL-9]** A `waive` holds from its `at` until its `expires`, compared with the time the view is computed; an
  expired waiver is shown as expired.
- **[OVL-10]** A `redact` withholds a blob only when it is **authorized**: the event is in a verified batch whose
  signature (`overlays/seal-<nnnn>.dsse.json`, §4.4) verifies for the event's `by.identity`, and the caller's trust
  policy allows that identity to redact (`"may": ["redact"]`, [SIG-4]). A sealed blob named in an authorized redaction
  may be deleted from the run, and seal verification then reports it as `withheld`, not `missing`. Without such a
  signature, or without a trust policy, a deleted blob is `missing` and the run is invalid: whoever can append an
  unsigned event cannot suppress evidence. Only blobs can be withheld; the rest of the run stays verifiable, and a
  reader **MUST** show a run with withheld blobs as "intact, *n* withheld", never as plain intact. This is how
  personal data in a captured blob is erased without breaking the seal or the checkpoints that rely on the run.
- **[OVL-11]** `conformance/overlay-views/` holds runs with events and the effective view each gives.

## 4.4 Signatures

- **[SIG-1] Envelopes.** A signature is a DSSE v1 envelope ([DSSE]): `payloadType`, `payload` (base64 of the signed
  bytes), and `signatures` (at least one, each a `keyid` and a base64 `sig`; an envelope with none is `malformed`).
  Base64 is written in the standard alphabet with padding; a reader also accepts the URL-safe alphabet, with or without
  padding, as DSSE requires, and refuses whitespace, set unused bits and mixed alphabets. The signed message is the DSSE pre-authentication
  encoding `PAE(payloadType, payload)`:

  ```
  "DSSEv1" SP LEN(payloadType) SP payloadType SP LEN(payload) SP payload
  ```

  with lengths in ASCII decimal bytes.

  | Envelope | Payload: the exact bytes of | `payloadType` |
  |---|---|---|
  | `attestation.dsse.json` | `seal.json` | `application/vnd.in-toto+json` |
  | `overlays/seal-<nnnn>.dsse.json` | `overlays/seal-<nnnn>.json` | `application/vnd.in-toto+json` |
  | `<checkpoint>.dsse.json`, beside a checkpoint manifest | the manifest | `application/vnd.agenteval.aef.checkpoint+json` |

  A verifier **MUST** check that the payload is byte for byte the file it signs, and that `payloadType` is the one this
  table gives for that file (`payload-mismatch` otherwise).
- **[SIG-2] Algorithms.** A verifier **MUST** support ECDSA over NIST P-256 with SHA-256 ([FIPS 186-5]; the signature
  is DER-encoded `SEQUENCE { r INTEGER, s INTEGER }`, strictly: a raw `r‖s` is not a signature), and **SHOULD** support
  Ed25519 ([RFC 8032]). A signer uses one of these two. ECDSA has no low-S rule here: `s` and `n − s` are both valid,
  as in DSSE and in-toto. A trusted key of any other algorithm gives `unsupported-algorithm` for a signature it would
  check, which counts as not verified.
- **[SIG-3] Key ids.** `keyid` is `sha256:` and the hex SHA-256 of the public key's DER-encoded SubjectPublicKeyInfo.
- **[SIG-4] Trust policy.** Which keys to trust is an **input** to verification, never something read from the run: a
  list of public keys, each with the identity it speaks for (for example `git:alice@example.com`, `spiffe://…`,
  `oidc:issuer/subject`) and, optionally, what that identity may do beyond signing: `"may": ["redact"]` allows it to
  authorize redactions ([OVL-10]). A verifier **MUST NOT** trust a key because a run, an overlay or a runner manifest
  names it.
  Keyless signing (Sigstore: a short-lived certificate tied to an OIDC identity, logged in a transparency log) **MAY**
  be supported as a trust-policy input; its bundle is then given beside the envelope.
- **[SIG-5] Results per signature**, in envelope order: `verified` (a trusted key, a valid signature: the identity is
  reported), `untrusted-key` (a valid signature by a key the policy does not list, or no key with that id), `invalid`
  (the signature does not verify), `unsupported-algorithm`, and, for the whole envelope, `malformed` (no per-signature
  results then) or `payload-mismatch`. A signature with a `keyid` is checked only against the trusted key with that
  id; one without a `keyid` (absent or empty, as DSSE allows) is tried against every trusted key in policy order, and
  its result names the first key that verifies it (or the empty `keyid` when none does: `untrusted-key`). An envelope
  **verifies for an identity** when it is neither `malformed` nor `payload-mismatch` and at least one of its
  signatures is `verified` for that identity.
- **[SIG-6]** `conformance/signature-vectors/` holds test keys (marked as test keys: never trust them), trust policies,
  envelopes over runs, overlay batches and checkpoints, and the result of each: valid, wrong key, tampered payload,
  unknown key, malformed signature, payload that is not the file.

## 4.5 Verification outcomes

A run verifier reports the run's **outcome**, every problem found, and, when its caller gives the inputs, the two
stronger levels:

- the outcome is **invalid** when there is any problem of §3.9, or of §4.1 other than `withheld`; otherwise
  **unsealed** when there is no `seal.json`, and otherwise **intact**;
- **signed by** the identities `attestation.dsse.json` verifies for under a trust policy the caller gives (§4.4),
  for an intact run;
- **anchored** when the caller gives a list of trusted run hashes (taken from verified checkpoints, a transparency
  log, or its own records) and an intact run's run hash ([SEAL-4]) is in it;
- **withheld**: the number of blobs withheld by authorized redactions, when there are any (a reader shows "intact,
  *n* withheld").

Overlay problems (§4.2) are reported by an overlay verifier and do not change a run's outcome.

| Outcome | Meaning | What it rules out |
|---|---|---|
| **unsealed** | no `seal.json` | nothing |
| **intact** | the seal verifies (§4.1) and the run keeps the rules across files (§3.9) | a change after sealing by someone who did not re-seal; files that contradict each other |
| **signed** by *identity* | intact, and `attestation.dsse.json` verifies for *identity* under the caller's trust policy (§4.4) | a change after sealing by anyone without the key: re-sealing is not enough |
| **anchored** | intact, and the run hash is recorded somewhere the caller trusts: a checkpoint that verifies for a trusted identity and lists this run with this run hash (§5.1), a transparency-log entry, or a list the caller supplies | substituting another run with the same `runId` |

- **[SIG-7]** An **intact** run proves internal consistency only: anyone can edit a file and re-seal. A reader **MUST
  NOT** describe an intact run as authentic, approved or attributable; it **MUST** say "intact (unsigned)" or show the
  identity that signed it.
- **[SIG-8]** A checkpoint's runs are anchored by the checkpoint: a verified checkpoint that names a run with its run
  hash establishes that this is the run the decision relied on (§5.5).
