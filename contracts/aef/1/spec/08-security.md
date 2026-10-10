# 8. Security and privacy

This document is partly normative (the requirements with ids) and partly an analysis, so implementers and users know
what AEF does and does not protect.

## 8.1 What is protected, and from whom

**Assets:** the results of a run; the decision a checkpoint records; the human decisions in overlays; the content a
run captured (prompts, responses, reasoning), which may be confidential or personal.

**Adversaries**, and what stops each:

| Adversary | Can | AEF's defence | Remaining risk |
|---|---|---|---|
| A **dishonest producer** | write any results it likes, then seal and sign them | none can prove results true; a signature makes the producer accountable, and `execution.targetMode` makes a stand-in visible | trust in the producer is the reader's decision |
| A **custodian** (storage, a host, a CI cache) | change files after sealing | the seal ([SEAL-6]) detects any change unless they re-seal; a signature ([SIG-7]) detects a re-seal; an anchor ([SIG-8]) detects a substituted run | a custodian that holds the signing key |
| An **overlay writer** | append an approval it was never given, claim an assurance it does not have | assurance is shown only as verified ([OVL-3]); batch signatures name who sealed each batch | an unsigned overlay is only a claim |
| An **appender** (append-only or shared storage, a crashed or concurrent writer) | append bytes after the sealed batches: half a line, blank lines, millions of lines | the events file is judged line by line and read only within [ENC-17]'s limits ([OVL-5]), so no append changes which batches verify or what they withhold, and a half line costs one line, never the chain | none: an appended event is shown as unsealed and has no effect |
| A **truncater** | remove the newest overlay batches with their seals | a reader that saw a longer chain, a signed newest batch, a copy held elsewhere ([OVL-5]) | a first-time reader of a truncated copy |
| A **substituter** | swap a run a checkpoint relied on for another with the same `runId` | the checkpoint records each run's run hash ([CKP-2]); the verifier checks it ([CKP-8]) | an unsigned checkpoint can itself be rewritten |
| A **parser exploit** | write a file two readers read differently (duplicate members, lone surrogates) | I-JSON is required and checked ([ENC-2]) | none known |
| A **resource exhaustion** attempt | write enormous files, deep nesting, millions of lines | limits ([ENC-17]) every reader enforces, checked on the bytes before the content is trusted | a reader that does not enforce them is not conforming ([ENC-18]) |
| A **runner** | overspend, run other cases, report runs it did not seal, return runs other than those planned | the stream verifier ([STRM-3]) against the plan's bytes, and plan conformance ([STRM-4]) against the runs themselves | a runner that lies consistently in stream and runs: only the signature on its runs attributes it |

## 8.2 What each mechanism does not do

- **A seal is not a signature.** Anyone can edit a file and compute a new seal. An intact run is internally
  consistent, nothing more ([SIG-7]).
- **A signature is not trust.** It names a key; the trust policy decides what that key may speak for ([SIG-4]).
- **Nothing in AEF makes a result true.** It makes it attributable and tamper-evident.
- **Overlays do not change the sealed run.** A reader that presents an override as the sealed result misleads
  ([OVL-7]).

## 8.3 Secrets

- **[SEC-1]** No AEF file holds a credential: not `run.json` ([RUN-10]), not a plan ([PLAN-4]), not an event, not
  `ext`. A runner gives credentials to processes as environment variables and never records them ([PLAN-3]).
- **[SEC-2]** A tool that finds a secret in a run **MUST NOT** seal it (a sealed run is kept); if already sealed, the
  run is withdrawn and re-produced. Redaction ([OVL-10]) covers blobs only.

## 8.4 Privacy

- **[SEC-3]** `contentCapture: off` keeps no content and no digest of content ([RUN-11]). Use it when prompts or
  responses may hold personal data and the evidence does not need them.
- **[SEC-4]** Identities in overlays (`by.identity`) **SHOULD** be stable opaque ids (an OIDC subject, a SPIFFE id, a
  key id) rather than e-mail addresses: an overlay is append-only and cannot be erased.
- **[SEC-5]** A captured blob holding personal data is erased with a `redact` overlay and by deleting the blob
  ([OVL-10]); the seal then reports it `withheld` and the rest of the run stays verifiable.
- **[SEC-6]** Traces (`traces.otlp.jsonl`) and logs (`logs.otlp.jsonl`) follow OpenTelemetry's GenAI conventions,
  which may carry prompt and completion text. In a run with `contentCapture: off`, no span, span event, log record,
  resource or scope (their `attributes`) carries `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`,
  `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`, `gen_ai.evaluation.explanation` (a judge's reasoning), or
  the deprecated `gen_ai.prompt` and `gen_ai.completion`, and no log record has a `body` member, whatever its value. A run verifier reports each
  such line as `content-capture` (§3.9). Content in an attribute these conventions do not name cannot be detected: a producer
  **MUST NOT** write it either.

## 8.5 Algorithms

- SHA-256 everywhere. ECDSA P-256 and Ed25519 for signatures ([SIG-2]). A new major version will be needed to change
  the digest algorithm; the `sha256:` prefixes make the algorithm explicit in every digest written.
