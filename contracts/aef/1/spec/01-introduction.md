# 1. Introduction

**AEF 1.0, the AgentEval Evidence Format.** Status: draft for 1.0, unreleased. This document is normative except
where it says it is not.

## 1.1 Purpose

An evaluation of an AI agent ends in a claim: *this version passed the safety suite*, *nothing critical was found*,
*the release was approved*. AEF is an open file format that lets **someone other than the tool that made the claim**
check it:

1. **That the evidence is the evidence.** A run of an evaluation is one folder of plain files (JSON, NDJSON, raw
   blobs, OpenTelemetry traces), sealed over its exact bytes. Any change to any file is detected by recomputation;
   with a signature, the sealer is identified too (§4).
2. **That a decision follows from the evidence.** A release decision ("may version X ship?") is a checkpoint: it names
   the exact sealed runs it relied on, and a published, pure decision function turns their results into an outcome
   anyone can recompute (§5).
3. **That a runner did what it was asked.** When an evaluation is delegated to a runner, the plan it was given and
   the stream of events it reported can be checked against each other: budget, limits, the runs it sealed (§6).

AEF does not judge whether an evaluation was well designed. It makes what was run, what came out and what was
decided **inspectable and tamper-evident**, so that judgement can be made by anyone.

## 1.2 Audience

- **Producers**: evaluation tools that write runs (AgentEval is one; any harness can be).
- **Hosts and dashboards** that collect, index and show runs (AgentEval Mission Control is one).
- **Verifiers**: auditors, CI systems and release processes that check seals, chains, checkpoints and streams.
- **Runners**: services that execute an evaluation plan on someone's behalf.

## 1.3 Scope and non-goals

In scope: the run folder and its files; sealing, overlays and signatures; checkpoints and the decision function; run
plans, runner manifests and the runner event stream; versioning; the conformance corpus.

Not in scope, by design:

- **A test-suite format.** AEF records which suite ran (its reference, version and content digest), not how suites are
  written.
- **A scoring method.** AEF records the scores, states and the rule a producer applied; it does not prescribe metrics,
  thresholds or aggregation (§3.4.3: aggregation is descriptive).
- **A trace format.** Traces are OpenTelemetry (OTLP/JSON); AEF links to them.
- **A transport or an API.** AEF is files. How they move (a copy, an upload, an artifact store) is outside it.
- **Identity management.** AEF says which keys signed what; which keys to trust is the verifier's policy (§4.4).

## 1.4 Relation to AgentEval

AEF was designed by the AgentEval project, which is its first producer (AgentEval 1.0 writes AEF 1.0) and whose
Mission Control reads it. The format has no dependency on AgentEval: nothing in this specification requires AgentEval
code, names or conventions, and the reference tools in `tools/` are standalone Python. AgentEval's older output
directory (`.agenteval/`, its "store v1", which predates AEF) is not AEF; §7.5 says how it maps.

## 1.5 Conventions

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**,
**RECOMMENDED**, **NOT RECOMMENDED**, **MAY** and **OPTIONAL** in this specification are to be interpreted as
described in BCP 14 ([RFC 2119], [RFC 8174]) when, and only when, they appear in all capitals, as shown here.

Each requirement has an identifier in brackets, such as [ENC-3], so test vectors, implementations and
conformance claims can name the rule they concern. Identifiers are stable: a later minor version never reuses one for
a different rule.

The JSON Schemas in `schemas/` and the corpus in `conformance/` are part of this specification. Where prose and a
schema disagree, the stricter one applies and the disagreement is a defect to report. A field the prose does not
mention is informative: its meaning is its schema description, and no rule depends on it.

Statements in the specification that use no BCP 14 keyword ("a run is a folder", "X is reported as Y") are
requirements too: they say what conforming files are and what a conforming implementation does. Notes, examples and
the documents outside `spec/` are informative.

Paths in this specification use `/` as the separator. "Byte" means an octet. Hexadecimal is lower-case.

## 1.6 Terminology

| Term | Meaning |
|---|---|
| **Run** | One execution of an evaluation: a folder holding `run.json` and the files of §3. |
| **Result** | One node of a run's result tree: a line of `results.ndjson`. |
| **Case** | One item of the evaluated suite; a case has one or more results (a tree, or several trials). |
| **Closed** | A run whose `status` is `completed` or `aborted`. A closed run's files never change (§3.1). |
| **Seal** | `seal.json`: an in-toto Statement listing the SHA-256 of every sealed file of a closed run (§4.1). |
| **Run hash** | The SHA-256 of a run's manifest (§4.1); it identifies the run's exact content. |
| **Overlay** | An event added to a closed run after it closed (an approval, a waiver, a note), in `overlays/` (§4.2). |
| **Checkpoint** | A release decision over evidence lanes for one exact subject version (§5). |
| **Lane** | One kind of evidence a checkpoint requires (a quality suite, a red-team campaign), with a rule and runs. |
| **Plan** | What a runner is asked to evaluate, with its limits (§6.1). |
| **Typed absence** | A result state that records that something was not measured, and why; never a pass, never a zero (§3.4.1). |
| **Writer schema / reader schema** | The strict schemas a producer MUST meet, and their tolerant derivation a reader accepts (§7.1). |

## 1.7 Roles and conformance classes

An implementation claims conformance to one or more **classes**. §9 lists the requirements and the corpus vectors of
each; a claim names the classes, the AEF version and the corpus version it passed.

| Class | Does | Main sections |
|---|---|---|
| **Producer** | Writes runs: valid against the writer schemas, the encoding rules and the rules across files | 2, 3 |
| **Sealer** | Seals closed runs, and MAY sign them | 4.1, 4.4 |
| **Reader** | Reads runs: tolerant of later minors, fail-closed on unknown values | 2, 3, 7 |
| **Run verifier** | Recomputes a seal, checks the rules across files, and reports the verification outcome; at the *signed* level, also verifies signatures against a trust policy | 3.9, 4.1, 4.4, 4.5 |
| **Overlay verifier** | Verifies the overlay chain and computes the effective view | 4.2, 4.3 |
| **Checkpoint verifier** | Checks a checkpoint manifest, recomputes each lane's result from its sealed runs, and recomputes the decision | 5 |
| **Decision engine** | Implements the decision function alone | 5.4 |
| **Runner** | Accepts plans and writes event streams and sealed runs (*at risk* in 1.0, [§9.1](09-conformance.md#91-classes)) | 6 |
| **Stream verifier** | Checks a runner's event stream against its plan | 6.4 |

AEF's first consumer, AgentEval's Mission Control 2.0, relies on the Run verifier at the signed level, the Overlay
verifier, the Checkpoint verifier (and with it the Decision engine) and the Stream verifier; AgentEval itself is a
Producer and a Sealer of the runs they read.

## 1.8 Documents

| Document | Contents |
|---|---|
| [01 Introduction](01-introduction.md) | This document |
| [02 Encoding](02-encoding.md) | JSON and NDJSON, values, identifiers, patterns, limits |
| [03 The run](03-run.md) | The run folder and every file in it, the rules across files |
| [04 Integrity](04-integrity.md) | Sealing, overlays, signatures, verification outcomes |
| [05 Checkpoints](05-checkpoints.md) | Checkpoints, lane evaluation, the decision function |
| [06 Runners](06-runners.md) | Run plans, runner manifests, matching, the event stream, running a job |
| [07 Versioning](07-versioning.md) | Writers and readers, minors and majors, unknown values, migration |
| [08 Security and privacy](08-security.md) | Threat model, what is and is not protected, privacy |
| [09 Conformance](09-conformance.md) | Classes, the corpus, running it, claiming conformance |

## 1.9 Profiles

A **profile** builds on AEF for a narrower use: it adds files or rules for one kind of evidence, has its own `$id`
outside `/aef/1/`, and is versioned on its own. A profile never changes what AEF requires, and no conformance class of
§9 requires a profile. The runtime-verdict profile (AEVP 0.1, `profiles/runtime-verdict/`) is the first; it is not part
of AEF 1.0.

## 1.10 References

- [RFC 2119] Key words for use in RFCs to Indicate Requirement Levels. [RFC 8174] Ambiguity of Uppercase vs Lowercase
  in RFC 2119 Key Words.
- [RFC 3339] Date and Time on the Internet: Timestamps. [RFC 7493] The I-JSON Message Format. [RFC 8259] The JSON Data
  Interchange Format.
- [JSON Schema] JSON Schema Draft 2020-12. [ECMA-262] ECMAScript Language Specification (regular expressions).
- [in-toto] in-toto Attestation Framework, Statement v1. [DSSE] Dead Simple Signing Envelope, protocol v1.
- [OTLP] OpenTelemetry Protocol, JSON encoding. [OTel GenAI] OpenTelemetry semantic conventions for generative AI.
- [FIPS 186-5] Digital Signature Standard (ECDSA P-256). [RFC 6979] Deterministic ECDSA. [RFC 8032] Ed25519.
