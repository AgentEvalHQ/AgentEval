# AEVP 0.1 — Agent Evidence Profile

**Status:** draft profile — not a standard; no standards body or AGENT-HOOKS maintainer has reviewed it · **Applies to:** AGENT-HOOKS-0.1 · **Schema:** `aevp-0.1.schema.json`

A **profile** for the evidence an interceptor attaches to its verdicts. It is offered as an *interceptor
specification* in the sense AGENT-HOOKS-0.1 §1 intends, but it is one project's proposal, not a standard:

> "This specification does **NOT** define how an interceptor computes a verdict. Policy languages, manifests,
> dispatchers, annotators, and information-flow lattices are out of scope and are defined by **interceptor
> specifications** such as the Agent Control Specification (ACS)."

ACS answers *what the policy said*. AEVP answers the orthogonal question: **how much should this verdict be
trusted?**

---

## 1. The slot this fills

AGENT-HOOKS-0.1 §5.3 defines `verdict.evidence` as:

> "an opaque pointer to an offline-verifiable **artefact supporting the verdict**. A host MUST propagate
> `evidence` to its audit sink unchanged when present."

The schema gives that pointer exactly two members — `artefact` (a `sha256:<hex>` content address or URI) and
`verification_pointers`. **It never defines what the artefact contains.**

`AGT-EVIDENCE-1.0` covers proof artefacts and verification pointers — the plumbing — and contains no notion of
confidence, calibration, coverage, evidence tier or abstention.

So: *how to attach evidence* is solved. **What the evidence must say is unclaimed.** AEVP is the artefact.

---

## 2. Why it exists — the abstention problem

AGENT-HOOKS-0.1's decision enum is closed:

```json
"decision": { "enum": ["allow", "deny", "transform"] }
```

and both `Verdict` and `InterceptionRecord` are `additionalProperties: false` with **no extension mechanism**
(`AgentContext` has one; the interceptor→host direction does not).

**An interceptor that could not evaluate a call must still return `allow` or `deny`.** Either way, the verdict
on the wire records a decision the interceptor did not actually reach: the host, its audit sink and a later
reader cannot tell "examined and allowed" from "could not examine, defaulted to allow". There is no conformant
way to abstain, and AEVP does not add one. It lets the evidence say what the verdict cannot. A first-class
abstention would be an AGENT-HOOKS change (a proposal of its own), and if one is accepted, AEVP's `evaluated`
field becomes redundant while its other four fields are unaffected.

This is not an inference from the schema — the reference core says so directly. Submitting an abstention to
`ah_validate_verdict` (the same call the SDK's own emitter makes before dispatch) returns:

```
host_error:verdict_invalid: verdict.decision invalid: Some("abstain") (§5.1: allow|deny|transform)
```

while a bare `{"decision":"allow"}` is accepted. The gap is enforced by the implementation, not merely
permitted by the prose. Reproduced by `CtkNativeSmokeTests.TheValidatorRejectsMalformedVerdicts_SoAGreenRunIsNotVacuous`.

The need has been independently rediscovered at least three times: AgentEval's `Inconclusive`; ASSERT's
`allow_not_applicable`; and an OpenTelemetry semconv contributor observing that "custom right now carries two
opposite meanings, this came from an observed outcome and nobody classified this." SARIF, an OASIS standard,
solved the same problem in 2019 by separating evaluation **state** (`result.kind`: `notApplicable | pass |
fail | review | open | informational`) from **severity** (`result.level`).

AEVP's `evaluated: false` recovers that distinction **without any change to agent-hooks**: the verdict still
says `allow`, but the artefact says nobody checked. An auditor can tell *permitted* from *unexamined*.

---

## 3. The five fields

Deliberately capped. Each answers a question an auditor must ask and cannot ask today.

| Field | Required | Answers |
|---|---|---|
| `evaluated` | ✅ | **Did you actually check?** `false` ⇒ the verdict carries no evidence of safety |
| `enforcement_capability` | ✅ | **Could you even have stopped it?** `observe` \| `block` \| `transform` — what the seam could physically do, not what it chose |
| `evidence_tier` | — | **How strong is the evidence?** `verbal` \| `intent_to_act` \| `behavioral`. MUST be absent when `evaluated: false` |
| `judge` | — | **Who decided?** `{ id, version?, model? }`. Absent for deterministic deciders |
| `calibration` | — | **Are they any good?** `{ kappa, decisive_accuracy, dangerous_errors, n, baseline_beaten }`. **Absent means UNCALIBRATED** — a weaker claim, not a neutral one |

> **The cap is the discipline.** `additionalProperties: false` enforces it mechanically. If this grows past
> ~8 fields, stop — an unadoptable profile is worth less than a small adopted one.

---

## 4. Invariants

1. **`evaluated: false` ⇒ no `evidence_tier`.** Claiming a tier for a call nobody evaluated asserts evidence
   that was never gathered. Enforced by the schema, pinned by vector **AEVP-003**.
2. **Unknown `aevp` version ⇒ reject.** A consumer must not partially interpret an unrecognised profile
   (**AEVP-004**).
3. **Unknown fields ⇒ reject** (**AEVP-005**).
4. **The artefact is content-addressed.**
   - The address is `sha256:<hex>` over the profile's **RFC 8785 (JCS)** canonical UTF-8 bytes, so any JCS
     implementation computes the same address (**AEVP-007**).
   - The reference interceptor writes each profile to an `IAevpArtifactStore` before it returns the address, so
     the address resolves (**AEVP-008**).
   - The default store is in-memory; supply a durable one when auditors outside the process must resolve it.
5. **Absent `calibration` is not "fine".** It means the decider never underwent calibration.

---

## 5. Conformance vectors

Eight numbered vectors ship with the reference implementation
(`tests/AgentEval.Tests/MAF/AgentHooks/Aevp/AevpVectorTests.cs`), in the style of agent-hooks' own `AH-CTK-*`
corpus. They exist to prove the profile is **checkable**: a claim about trustworthiness that no tool can
falsify is worth nothing.

| Vector | Pins |
|---|---|
| AEVP-001 | An evaluated profile is schema-valid |
| AEVP-002 | An unevaluated profile is valid and carries no tier |
| AEVP-003 | **Unevaluated + evidence_tier is REJECTED** |
| AEVP-004 | Unknown profile version is rejected |
| AEVP-005 | Unknown fields are rejected (the cap) |
| AEVP-006 | Calibration round-trips and validates |
| AEVP-007 | Content address is stable and content-sensitive |
| AEVP-008 | The reference interceptor emits a resolvable profile on every verdict |

---

## 6. Reference implementation

`AgentEval.MAF.AgentHooks` — Gatekeeper exposed as an AGENT-HOOKS-0.1 interceptor. It attaches a profile to
every verdict it produces:

- decided at the tool seam → `evaluated: true`, `enforcement_capability: transform`,
  `evidence_tier: intent_to_act`, `judge: { id: <gate policy name> }`
- an interception point with no gate registered → `evaluated: false`,
  `enforcement_capability: observe`, **no tier**

Deterministic gates emit **no `calibration`** — asserting a calibration record they never underwent would be
exactly the fabrication this profile prevents.

---

## 7. Status and non-goals

Draft, and deliberately unpublished pending a decision on the upstream route. AEVP does **not** define policy
languages, interception points, verdict semantics, or host obligations — those are AGENT-HOOKS-0.1's, and
duplicating them would be a competing contract rather than a complementary profile.
