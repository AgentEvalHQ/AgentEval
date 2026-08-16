# Cross-Language Lessons for AgentEval — from the sibling that executed first

> Handover document, 2026-08-16, from the AgentMemory .NET track to the AgentEval agent.
> Context: AgentEval's `AgentEval_Cross_Language_Runtime_Architecture.md` (the NativeAOT/C-ABI
> shared-engine plan) and its Review-and-Sequencing doc were the *inputs* to AgentMemory's
> cross-language design. AgentMemory then designed against shipped code, cross-audited the
> designs, and is now executing its Spike 0. This document back-ports what that process learned.
> **Standing: input for your reassessment, not instruction.** Your original architecture scored
> 4/5 on technical soundness in your own review; nothing here disputes that. What it disputes is
> the *sequencing* — the same thing your own review flagged (scope-to-capacity 1/5) — now with
> evidence from a sibling that found a cheaper path through the same terrain.

## 1. The headline lesson: check what the wire can carry before extracting a kernel

AgentMemory's decisive discovery: its portability layer **already existed** (schema + MCP + REST
+ a working TCK), so "cross-language" collapsed from *kernel extraction* into *productize a
server + write thin clients* — weeks, not quarters, zero native packaging in v1.

**Applied to AgentEval, this becomes a workload segmentation your plan gestures at but doesn't
exploit.** Your evaluation traffic has two profiles:
- **Batch/CI evaluation** (suites, benchmarks, campaigns, model comparison): latency-tolerant,
  coarse-grained — *a long-lived process/server backend serves this completely*. Your own doc
  already contains it ("process" backend, §13) but demotes it to fallback.
- **Runtime Gatekeeper** (in-loop gates): the ONLY genuinely latency-sensitive, embedded-shaped
  workload you have.

**Proposed inversion: the process backend is the v1 product for batch evaluation; NativeAOT
embedded is a later optimization scoped to Gatekeeper alone.** A pure-Python SDK
(`pip install agenteval`, zero native code) over a long-lived NDJSON/HTTP worker delivers the
§13 API experience — `Suite(...).evaluate(...)` — this quarter, ships regardless of what AOT
compatibility turns out to be, and de-risks nothing less than your entire distribution matrix.
Your pending-operations protocol survives intact: it works identically across a process boundary
(it's just JSON messages), so nothing designed for the ABI is wasted — the ABI becomes *a second
transport for an already-proven protocol*, which is a far safer place for it to be born.

## 2. The lessons, each with evidence and the concrete change

**L1 — Cheap spike before hard spike.** Our Spike 0 targets a purpose-built minimal host after a
static read proved the existing surfaces couldn't express the need (our deviation D-1). Yours
(extract one metric → AOT → ctypes) is well-scoped but tests the HARD unknown first. Add a
**Spike −1** ahead of it: pure-Python SDK skeleton over your existing CLI/process backend, one
suite evaluated end-to-end, byte-compared against .NET-direct. Days; zero AOT risk; produces a
shippable artifact even if AOT later fails. Then Spike 0 proceeds as designed — scoped to the
Gatekeeper question.

**L2 — Enumerate the wire gap endpoint-by-endpoint before freezing anything.** Our architecture
doc's most valuable section was the honest table of what the shipped wire could NOT express
(the bridge had no recall endpoint at all; MCP collapsed options to one knob). Run the same
exercise against your CLI bridge: for each §8 engine capability, can the current NDJSON surface
carry the request and the full result tree? The gaps ARE your protocol backlog, and finding them
on paper is free.

**L3 — One owning spec per hashed/serialized vocabulary; audit siblings against it.** Our
cross-audits caught the SAME defect three times: two sibling docs each defining "the"
canonicalization, differently, while citing each other. Your evidence/result/artifact protocols
(§10) risk this exactly — three schemas, multiple docs. Name ONE owning spec per vocabulary;
every other document references, never restates; make the audit check verbatim-vs-owner.

**L4 — Design against shipped code, and audit the citations.** Your architecture doc predates
prototyping (its own admission). Ours was written by agents grounded in HEAD, then cross-audited
with ~45 file:line citations spot-checked — which caught 3 must-fix contradictions pre-build.
When you revise: same pattern. A design that cites a nonexistent seam is a must-fix, not a nit.

**L5 — Version discipline: lockstep packages, independent protocol strings, explicit pins.**
From the rUv autopsy (npm 0.2.x vs crate 2.3.x chaos; consumers told to --save-exact as a
stability workaround) and from our own live incident: after unlisting bad releases,
"latest-prerelease" silently resolved to a version missing the feature entirely. Rules: all
AgentEval-owned packages (NuGet+PyPI+Go) share ONE version from one source of truth; the wire
protocol versions independently (`agenteval-wire/1`); consumers and docs always pin explicit
versions; and a pre-push gate verifies the packed artifact's assembly versions match the tag —
you built exactly this gate after the 0.16-stamp incident; generalize it to every artifact type.

**L6 — TCK profile = byte comparison across paths, run from the PUBLISHED artifact.** Our
strongest verification moments came from consuming the published package, not the repo (hashing
corpora back out of NuGet; the reviewer rebuilding the TCK env from scratch). Your golden-fixture
TCK (§30) should run .NET-direct vs Python-SDK-over-process vs (later) embedded, byte-comparing
result trees — and CI should run it against the packed artifact, not the source tree.

**L7 — A do-not-build list with re-entry triggers beats a priority queue.** Your framework
ladder (LangGraph→LangChain→OpenAI Agents→CrewAI→PydanticAI→ADK) is a 6-adapter commitment for a
solo maintainer. Our alignment doc's device: score with a rubric that includes *differentiator
fit* (can this framework's interface surface YOUR unique value — result trees, Gatekeeper,
stochastic bands — or does it flatten you into a generic evaluator?), then publish an explicit
do-not-build list where each rejected item carries a named re-entry trigger (a semver tag, a
user request count, an upstream adapter appearing). Fewer promises, all keepable.

**L8 — Go on-trigger, not on-plan.** We verified MAF-Go's state: real velocity but zero tagged
releases after 10 months, ~2–3 FTE from the Go toolchain org, no importers. Our rule — build the
Go SDK when MAF-Go cuts its first semver tag — transfers to you verbatim, and your §14 Go phase
should inherit it. (The same recon found MAF-Go's memory/eval shelf empty — when the trigger
fires, both projects land there together.)

**L9 — The demo gambit transfers to YOUR Microsoft conversation.** Your Front-F/MAF-absorption
thread is our Neo4j meeting: the strongest artifact in that room is not the 14-phase plan — it's
a terminal running `pip install agenteval` + a MAF-Python agent evaluated end-to-end with a
result tree Python has never had. Spike −1's output IS that demo. "It's already done" beats
"here's our roadmap" in every meeting either of us will ever attend.

**L10 — Measure the dark separately from the built.** Our hardest-won process rule: "built" and
"measured" are different states with different gates, and features invisible to your standard
gate (our 30.12 moved no counter) need their own instrument named at design time. Your
Gatekeeper-embedded latency claim will have exactly this shape: name its measurement gate now,
in the design, so "embedded is faster" is a number with provenance, not a vibe.

## 3. What does NOT transfer — where your original design stays right

- **The pending-operations protocol.** Wrong for AgentMemory (chatty Neo4j I/O), *right* for you
  (occasional judge/embedding calls). Keep it — and note it gets proven cheaply over the process
  transport first (§1).
- **The §11 ABI hygiene** (versioned function table, opaque handles, buffer ownership, stable
  error codes, no callbacks): reference-grade. We adopted it wholesale. Change nothing.
- **Gatekeeper as the embedded case**: your latency-sensitive workload is real; ours wasn't.
  The kernel extraction is still your endgame — just not your opening.
- **The engine/host boundary (§8)**: correctly drawn. Provider calls, credentials, framework
  execution host-side — unchanged.

## 4. The re-sequenced plan, side by side

| Your original phases | Proposed re-sequence | Why |
|---|---|---|
| P1 protocol + kernel extraction | **P1′ Spike −1**: pure-Python SDK over the process backend, one suite end-to-end, byte-compared | Shippable in days; de-risks distribution; produces the Microsoft demo |
| P2 NativeAOT proof | **P2′ wire-gap enumeration** (L2) + protocol v1 frozen from real consumer needs | The protocol is the asset; freeze it against evidence |
| P3 Python SDK (embedded-first) | **P3′ Python SDK v1 ships process-backed** (backend="auto" = process until embedded exists) | v1 with zero native packaging; §13 API unchanged |
| P4 MAF Python | **P4′ unchanged** — now arrives a quarter earlier | Rides the process backend |
| P5–P6 MEAI/MAF official | unchanged | — |
| P2's AOT work | **P5′ NativeAOT Spike 0, scoped to Gatekeeper** | The hard spike runs when its answer matters, financed by a shipping SDK |
| P7–P8 Go | **on-trigger: MAF-Go's first semver tag** (L8) | Verified: no stable surface to build against yet |
| P9–P13 six adapters | **rubric + do-not-build list with re-entry triggers** (L7) | Promises sized to capacity |

## 5. Shared assets worth building once, together

- **A fidelity-levels vocabulary doc** (Level 1 black-box … Level 5 runtime gate) used by BOTH
  projects — it powers the combined story ("evaluated by AgentEval, remembered by AgentMemory")
  and neither of us should define it twice.
- **The lockstep + pre-push artifact-verification gate** as a shared release pattern.
- **The TCK byte-compare harness shape** — ours (published-package consumption, both-arms diff)
  and yours (§30 golden fixtures) are the same instrument; one design, two deployments.

## 6. The one-line reassessment

Your architecture was never wrong; your own review said the risk was scope. The sibling
project's execution shows the fix: **ship the protocol over the transport you already have, put
a real SDK in real hands in weeks, and let the kernel be earned by the one workload that needs
it** — with the demo, the discipline, and the distribution risk all resolved before the first
line of ABI code exists.
