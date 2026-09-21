# ADR-033: Decision models are a third evaluator kind, not a chat provider

- **Status:** **Proposed (2026-09-20).** Proposed is a gate, not a placeholder (the ADR-026 / ADR-030
  precedent). The code in §4 is built and unit-tested. §7 (1) is **met for TypeSafe** the same day —
  thirteen real calls, every one HTTP 200, requests and replies in
  [`evidence/033-jev-first-calls-2026-09-20.md`](evidence/033-jev-first-calls-2026-09-20.md) — and
  **half-met for Bitdeer**: one real call answered correctly, then the account returned HTTP 402
  "insufficient balance", so the judged run is still owed. §7 (2) and (3) are untouched: no number
  about Jev's behaviour on AgentEval data exists yet, and none is estimated here.
- **Date:** 2026-09-20, written against `0e37b758` (v0.39.0-beta) on `main`.
- **Decision in one line:** **A structured decision model (TypeSafe's Jev is the first) enters
  AgentEval as its own transport (`IDecisionClient`) and its own leaf (`DecisionEval`,
  `Provenance.Type = "atomic-decision"`), beside — never inside — the deterministic and generative
  lanes; an OpenAI-compatible host of an ordinary model (Bitdeer's GLM-5.3 Flash) needs no new code at
  all, because `IChatClient` already reaches it.**
- **Relates to:** [ADR-030](030-meta-evaluation-is-the-lane.md) (the unified `IEval` tree this leaf
  joins; §4.2's rule that an undecidable is not a pass, which §4.3 below inherits);
  [ADR-032](032-benchmark-definition-run-score.md) (provenance labels that name what graded, which
  §4.4 extends to a third kind); [ADR-008](008-calibrated-judge-multi-model.md) (calibration before
  trust, which §6 defers to).

---

## 1. Context

Two design notes arrived on 2026-09-20 proposing (a) first-class support for GLM-5.3 Flash served by
Bitdeer, and (b) support for Jev, TypeSafe's "System One" decision model. Both were written against
the public AgentEval docs rather than the tree, and each contained claims this ADR corrects before
building on them:

| Claim in the notes | What was verified (2026-09-20) | Consequence |
|---|---|---|
| Jev's yes/no answer carries a `confidence` beside its probability | TypeSafe's API reference: a noul answer is `{ "type": "noul", "noul": 0.98 }` — the probability is the whole answer. Only choice and score answers carry `confidence`. | `DecisionEval` leaves `Score.Confidence` null. Nothing manufactures one. |
| OpenRouter reaches Jev at `POST /api/alpha/decisions` | OpenRouter's own SDK guide documents `POST https://openrouter.ai/api/v1/systemone`. Both paths answer 401 unauthenticated, so both exist; the SDK-documented one is the default. | `SystemOneClientOptions.OpenRouterEndpoint` = `/api/v1/systemone`; the endpoint is a plain `Uri`, so a caller can point at the other. |
| Build the result with `EvalResultFactory.Create(...)` | No such type. Results are built positionally, as `AtomicLlmEval` and `AtomicCodeEval` do. | `DecisionEval` builds the five-positional `EvalResult`. |
| A new provenance kind is a matter of choosing a string | `eval-result.schema.json` closes `provenance.type` to an enum with `additionalProperties: false`; a leaf carrying a new string is refused at persistence. | The enum gains `"atomic-decision"`, with a schema regression test, the way `"multi-judge-adjudicated"` was added. |
| GLM on Bitdeer needs a provider factory in Core | `AgentEval.Cli/Infrastructure/EndpointFactory.CreateOpenAICompatible` already builds any OpenAI-compatible endpoint from `--endpoint --model --api-key`, and `IChatClient.AsEvaluableAgent()` already accepts the result. | No library change. The Bitdeer sample constructs the client the same way the CLI does and says so. |

The architectural recommendation in the notes — treat GLM as an ordinary `IChatClient` and Jev as
something else — survives verification intact and is what this ADR adopts.

## 2. Why a decision model is not a chat client

An `IChatClient` produces assistant text; every judge in AgentEval then parses a verdict out of that
text (`ChatClientEvaluator` asks for JSON, retries once when it does not get it, and flags
`EvaluationFailed` when it still does not). A System One model does none of that. Its contract is:

```text
state + { id: typed question }  →  { id: typed answer }
```

- a **noul** question returns `P(yes)`;
- a **choice** question returns the winning option, the full distribution and a confidence;
- a **score** question returns a probability-weighted position on an ordered scale (2–10 levels on Jev), the
  distribution and a confidence. Levels are **0-indexed on the wire** (`"0"` is the first criterion)
  — observed, not documented; see the evidence file.

The thing that makes this useful to an evaluator is the probability. Pushing it through a chat
abstraction would flatten it into text and have the judge lane parse it back out — losing exactly
what was paid for. So the transport gets its own interface, and the eval gets its own kind.

## 3. What was NOT built, and why

- **No cascade (`EscalatingEval`, "Jev decides whether the LLM runs").** A cheap judge that selects
  which cases the strong judge sees makes its own errors invisible: the subtle security violation it
  scores at 0.94 never reaches the reviewer. That is the selective-verification shape ADR-028's
  "accept on the thing, calibrate on the proxy" rule exists to refuse. It is worth testing — and only
  after §7's calibration data exists.
- **No Jev as aggregator.** The five aggregation strategies are explicit and reproducible; replacing
  them with a learned verdict would be an opaque composite.
- **No batching in `CompositeEval`.** The composite runs arbitrary `IEval` leaves independently; teaching
  it to coalesce Jev leaves into one request would couple the generic scheduler to one provider.
  Batching is available today at the transport (`DecisionRequest` carries many questions) and can
  become a multi-question leaf later without touching the composite.
- **No provider-specific GLM code.** There is no technical reason for `AddBitdeer()` to exist.
- **No hidden retries in the transport.** A 429 or 529 is thrown with `IsTransient = true`. A retry
  that happens inside the client silently doubles the latency and cost the eval then reports as
  fact.

## 4. Decision

### 4.1 Abstractions (`AgentEval.Abstractions/Decisions/`, namespace `AgentEval.Decisions`)

`IDecisionClient.DecideAsync(DecisionRequest) → DecisionResponse`. Provider-neutral names on purpose:
`IDecisionClient`, `DecisionQuestion`, `DecisionAnswer`, `BinaryQuestion` — not `IJevClient` or
`NoulQuestion` (renamed 2026-09-20: "noul" is TypeSafe's wire name and stays in the transport). AgentEval owns
the concept; a provider package owns a protocol. Provider maximums (255 options, 10 levels on Jev) are
enforced by the transport, not baked into the contract, so the same records can front another provider. The records validate at construction (a probability is
finite and in [0, 1]; a choice has at least 2 options; a score has at least 2 levels; a request has at least
one question), so an out-of-range value cannot exist as an object.

### 4.2 Transport (`AgentEval.Core/Decisions/SystemOneDecisionClient.cs`)

One class serves both providers, because they speak the same body and differ only in base URL and
model id:

| | TypeSafe direct | OpenRouter relay |
|---|---|---|
| Endpoint | `https://api.typesafe.ai/v1/systemone` | `https://openrouter.ai/api/v1/systemone` |
| Default model | `jev-latest` (their documented flagship alias); every reply on 2026-09-20 named `jev-1.13.0`, and a request pinned to `jev-1.13.0` was accepted | `typesafe/jev-1.13` (pinned) — not yet exercised |
| Auth | `Authorization: Bearer <key>` | same |
| Extra reply fields | — | `id`, `provider`, `usage.cost` |

The parser is strict: every question asked must come back with an answer of the matching type, a
choice answer must select one of the options asked and carry a probability for every one of them, and a
score answer must carry a probability for every level asked and none other (added 2026-09-20 after the
same rule was written into the Agent Framework transport), or the call throws
`DecisionClientException(InvalidResponse)`. The provider's call id, when it reports one, is kept as
`DecisionResponse.ResponseId` for provenance. Failure kinds map from HTTP status
(401/403 → Authentication, 400/422 → InvalidRequest, 429 → RateLimited, 529 → Overloaded, 5xx →
ProviderUnavailable). The API key is redacted from any body the client quotes.
`RenderRequest(request, model)` returns the exact bytes a call would send, through the same
serializer, so a dry run prints every payload before the first paid call.

It lives in Core rather than in a new `AgentEval.Providers.Jev` package because it adds no
dependency (`HttpClient` and `System.Text.Json` are in the BCL). Moving it out later is a file move.

### 4.3 Leaf (`AgentEval.Core/Evals/DecisionEval.cs`, namespace `AgentEval.Evals`)

`DecisionEval : AtomicEval` asks **one noul question** per evaluation and maps:

```text
Score.Value                              = P(yes), exactly as returned
Score.Passed                             = P(yes) >= passThreshold      (default 0.70, uncalibrated)
Score.Confidence                         = null                          (a noul answer carries none)
Details.Dimensions["decision.probability_yes"] = P(yes)                 (survives the threshold)
Provenance.Type                          = "atomic-decision"
Provenance.JudgeModel                    = the model id the PROVIDER echoes back
Provenance.EstimatedCost                 = usage.cost if reported, else JudgeCostMap("jev")
```

Severity mirrors `AtomicLlmEval` (`< 0.40 → high`, else `medium`; `failureSeverity` can raise it, never
lower it). A `DecisionClientException` **propagates**: an eval that could not ask its question has not
measured anything, and a 0.0 in its place would read as "looked and found nothing good" — the
undecidable-is-not-a-pass rule of ADR-030 §4.2, applied to the failure side.

Noul only, in this slice. Choice and score have no single obvious mapping onto a [0, 1] score with a
pass/fail, and inventing one before seeing data would be the mistake the notes warn against.

### 4.4 Provenance and schema

`"atomic-decision"` is added to the v1 schema's `provenance.type` enum. This is additive: every
document written so far still validates. An older reader that validates a new document containing
the new kind will refuse it — which is the correct outcome for a reader that cannot name what graded.
The HTML and PDF renderers print `Provenance.Type` verbatim and need no change; Mission Control's
pill classifier does not name the new kind and shows it as its raw string.

### 4.5 GLM-5.3 Flash on Bitdeer

No library change. Verified 2026-09-20 by their docs, an unauthenticated probe, and one real call
(reply `ready`, model id echoed, 20 in / 79 out — the model reasons by default and the reasoning is
billed as output; the account then answered HTTP 402 "insufficient balance"):

```text
Base URL   https://api-inference.bitdeer.ai/v1        (OpenAI-compatible; tool calling documented)
Model id   zai-org/GLM-5.3-Flash
Auth       Authorization: Bearer <BITDEER_API_KEY>
```

CLI, today: `agenteval eval --dataset <d> --endpoint https://api-inference.bitdeer.ai/v1 --model zai-org/GLM-5.3-Flash --api-key $BITDEER_API_KEY`.

Library: `new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }).GetChatClient(model).AsIChatClient()`, then `.AsEvaluableAgent(name: "glm-5.3-flash@bitdeer")` for a subject or `new ChatClientEvaluator(client)` for a judge. Provider identity stays in the agent name because serving infrastructure changes latency, throughput, quantisation and tool-call reliability, and a benchmark that hides it is describing a model that does not exist. Bitdeer's list price for the model was not verifiable from their public pages on 2026-09-20; the sample takes it from `BITDEER_PRICE_INPUT_PER_1M` / `BITDEER_PRICE_OUTPUT_PER_1M` and prints "not priced" otherwise, rather than applying `JudgeCostMap`'s default rate to a model it does not know.

## 5. Consequences

**Positive.** AgentEval's evaluator taxonomy becomes `deterministic + probabilistic-decision +
generative`, which is a stronger claim than `code vs LLM`. A composite can carry all three as
independent evidence with the aggregation unchanged. The probability survives into the persisted
result, so calibration analysis is possible without re-running.

**Negative / open.** One more provenance kind for every consumer to know about. A leaf whose default
threshold nobody has calibrated. A second transport surface (HTTP, not `IChatClient`) to keep
resilient. OpenRouter's relay path is documented and probed (401) but has not carried a real call.
A single-call noise floor of about 0.01 on a repeated identical request, which a calibration run
must sample past.

## 6. Alternatives considered

- **Jev implements `IChatClient`.** Rejected: §2.
- **Put the transport in a new NuGet package now.** Deferred: it adds nothing today and costs a
  packaging pipeline; the namespace and folder already isolate it.
- **Map choice/score to a [0, 1] score in this slice.** Deferred: no mapping is obviously right,
  and the noul mapping is.
- **Use `"atomic-llm"` provenance for the new leaf to avoid touching the schema.** Rejected: it would
  name a judge that did not grade, the defect ADR-032 removed three instances of.

## 7. What acceptance requires

1. One real call to each provider from the N2 sample, with the rendered request and the raw reply
   kept beside this ADR (redacted). **TypeSafe: done 2026-09-20. Bitdeer: smoke call done; the
   judged run (N1 step 3, N2 stage 5 with the GLM leaf) is owed once the balance allows.**
2. A shadow run of `DecisionEval` beside an existing `AtomicLlmEval` leaf on one composite family
   with labels (GDPR or Agentic), reporting agreement, false-pass rate on high-severity cases, Brier
   score, latency and cost — per shape, not as a mean. **Agentic half, in substance, 2026-09-21:**
   sample N3 scored Jev beside GLM-5.3 Flash on 298 labelled cases through the evaluators' own rubrics,
   every named metric per category — [`evidence/033-n3-judge-vs-judge-2026-09-21.md`](evidence/033-n3-judge-vs-judge-2026-09-21.md).
   It ran through a judge adapter, not `DecisionEval` leaves in a composite; that form, and the
   compliance family, are still owed.
3. Only then: a threshold chosen on held-out data, and a decision on whether an escalation primitive
   is worth building.
