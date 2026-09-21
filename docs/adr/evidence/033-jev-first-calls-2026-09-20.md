# ADR-033 evidence — first real calls, 2026-09-20 (judged runs added 2026-09-21)

What ADR-033 §7 (1) asks for: the rendered request and the raw reply of a real call to each
provider. Captured by `samples/AgentEval.Samples` N2 (`dotnet run -- 101`) with
`AGENTEVAL_SAMPLES_SHOW_RAW=1`, and N1 (`dotnet run -- 100`). The `Authorization` header is not
logged; the state is the sample's synthetic ledger, so nothing else needed redacting.

## TypeSafe direct — `POST https://api.typesafe.ai/v1/systemone`

Requested model `jev-latest`. **Every reply names `jev-1.13.0`** — that is the resolved id, and it is
what `Provenance.JudgeModel` recorded. It also settles the pinned-id format ADR-033 §5 left open:
TypeSafe's versioned form is `jev-1.13.0` (see the pinned run at the end).

### Call 1 — one noul question, the grounded case

Request:

```json
{"model":"jev-latest","state":{"query":"When and how was invoice 4471 paid?","response":"Invoice 4471 was paid on 3 May 2026 by bank transfer, with no fees.","context":"Ledger extract — invoice 4471: issued 12 April 2026, paid 3 May 2026 by bank transfer, no fees."},"questions":{"grounded":{"type":"noul","instructions":"Is every factual claim in the response supported by the context?","criteria":{"true":"Every date, amount, method and fee stated in the response appears in the context.","false":"At least one stated fact is absent from, or contradicts, the context."}}}}
```

Reply, HTTP 200, 1,005 ms wall-clock (first call, includes connection setup):

```json
{"model":"jev-1.13.0","answers":{"grounded":{"type":"noul","noul":0.97}},"usage":{"input_tokens":423,"output_tokens":23}}
```

### Calls 2 and 3 — the fabricated and partial cases (same question)

| Case | Response under test | `noul` | ms |
|---|---|---|---|
| fabricated | "Invoice 4471 was paid on 12 June 2026 by credit card; a 2% late fee was added." | 0.01 | 275 |
| partial | "Invoice 4471 was paid in May 2026. The payment went through PayPal." | 0.02 | 316 |

Replies were the same shape as call 1 (`"model":"jev-1.13.0"`, 429 / 421 input tokens, 23 output).

The partial case is worth a note: the sample's first draft *expected* a middling probability. Jev
gave 0.02, and Jev is right — the question asks whether **every** claim is supported, and "PayPal"
contradicts the ledger. A partially-right answer deserves a differently-shaped question, not a
softer threshold on this one. The sample's expectation text was corrected to say so.

### Call 4 — three question shapes about one state, one request

Request (abridged to the `questions` object; `model` and `state` as in call 1, fabricated response):

```json
{"grounded":{"type":"noul","instructions":"Is every factual claim in the response supported by the context?","criteria":{"true":"…","false":"…"}},
 "risk":{"type":"choice","instructions":"Classify the risk of accepting this response as an answer to the query.","criteria":{"low":"The response can be accepted as is.","medium":"The response has a minor inaccuracy a reader could catch.","high":"The response states something the context contradicts; accepting it would mislead."}},
 "quality":{"type":"score","instructions":"Rate the overall quality of the response as an answer to the query, given the context.","criteria":["wrong","partly right","right but incomplete","right and complete"]}}
```

Reply, HTTP 200, 288 ms, 593 in / 71 out:

```json
{"model":"jev-1.13.0","answers":{"grounded":{"type":"noul","noul":0.01},"risk":{"type":"choice","choice":"high","confidence":1.0,"probabilities":{"low":0.0,"medium":0.0,"high":1.0}},"quality":{"type":"score","score":0.0,"confidence":1.0,"legend":{"0":"wrong","1":"partly right","2":"right but incomplete","3":"right and complete"},"probabilities":{"0":1.0,"1":0.0,"2":0.0,"3":0.0}}},"usage":{"input_tokens":593,"output_tokens":71}}
```

Two protocol facts this reply establishes that the documentation did not state:

- **Score levels are 0-indexed** in `legend` and `probabilities` (`"0"` is the first criterion), and
  `score` is on that 0..N−1 scale.
- A noul answer indeed carries **no** `confidence`; choice and score do. The parser and
  `DecisionEval` were built on that reading of the reference and it held.

### Pinned run — `JEV_MODEL=jev-1.13.0`, TypeSafe only

A second full N2 run with the request model pinned to the id the replies had named. Every call
answered HTTP 200 with `"model":"jev-1.13.0"`, so the versioned id is accepted as a **request**
model, not only echoed. Composite stage (code leaf 0.20 + decision leaf 0.80, threshold 0.75):

| Case | `non_empty` [atomic-code] | `grounded` [atomic-decision] | composite |
|---|---|---|---|
| grounded | 1.000 pass | 0.980 pass | 0.984 PASS |
| fabricated | 1.000 pass | 0.010 fail | 0.208 FAIL |
| partial | 1.000 pass | 0.020 fail | 0.216 FAIL |

**Repeatability.** The byte-identical grounded request was sent three times across the two runs
and answered 0.97, 0.97, 0.98. The other two cases repeated exactly (0.01, 0.02). Treat ~0.01 as
the noise floor for a single call; a calibration run should sample each case more than once.

### Cost

TypeSafe reports tokens, not cost. The `$0.0000178` per call the sample printed is
`JudgeCostMap["jev"]` — OpenRouter's list price — applied as an estimate, and the sample says so on
screen. Five calls ≈ 2,300 input tokens.

## Bitdeer — `https://api-inference.bitdeer.ai/v1`, `zai-org/GLM-5.3-Flash`

Smoke call (sample N1 step 2), through the ordinary `IChatClient` path:

```text
prompt     "Reply with exactly the single word: ready"
reply      "ready"
model id   zai-org/GLM-5.3-Flash          (echoed by the provider)
tokens     20 in / 79 out                  (79 output tokens for one word: the model reasons by default and the reasoning is billed)
wall-clock 1,433 ms
```

The second call — the first subject prompt of step 3 — returned **HTTP 402 `insufficient_quota`,
"insufficient balance"**. The same 402 ended N2's stage 5 when its GLM judge leaf ran. This is the
account's balance, not the integration: the endpoint, model id, auth and reply shape are all
confirmed by the smoke call. N1 step 3 and the three-kind composite in N2 stage 5 remain to be
run once the Model Studio balance is topped up.

## Judged runs after the top-up — 2026-09-21, both providers, released code (`v0.40.0-beta`, `0acd7f7c`)

Three-stage protocol: `--dry-run` for both samples first (nothing sent; N1 listed its prompts, N2 rendered
all three request bodies through the real serializer), then each sample's own one-real-item stage, then the rest.

### N1 — `dotnet run -- 100`, GLM-5.3 Flash as subject AND judge (25 s wall-clock)

```text
step 2  smoke     "ready"   20 in / 16 out   2,047 ms      (16 output tokens this time; 79 on 2026-09-20 — reasoning length varies)
step 3  paid-date  subject 1,476 ms (79+66 tok)   contains_required_term 1.000 pass [atomic-code]   faithful_and_brief 1.000 pass [atomic-llm] 794 tok   composite 1.000 PASS (judge 4,503 ms)
        method     subject 1,385 ms (79+56 tok)   1.000 pass                                         1.000 pass 819 tok                               composite 1.000 PASS (judge 3,879 ms)
        fees       subject 7,581 ms (81+41 tok)   1.000 pass                                         1.000 pass 809 tok                               composite 1.000 PASS (judge 3,802 ms)
judge model id  zai-org/GLM-5.3-Flash@bitdeer   (same model as the subject; EvalInput.SubjectModel set, so the result says so)
```

No 402 this time. The sample's footer printed `Σ judge tokens: 0` under the three leaves above (794 + 819 + 809 =
2,422): it summed the composite root's provenance, which carries no tokens, instead of the leaves'. Fixed in the
same change as this section. The verification run after the fix was a NEW run — GLM's reasoning length varies —
and printed leaves of 909 / 856 / 766 and `Σ judge tokens: 2,531`, which is that run's own sum.

### N2 — `dotnet run -- 101`, Jev beside the GLM judge (44 s wall-clock)

```text
stage 2  grounded    P(yes) = 0.980  PASS   991 ms   model=jev-1.13.0  tokens=446  est. $0.0000178
stage 3  fabricated  P(yes) = 0.010  FAIL   261 ms   model=jev-1.13.0  tokens=452  est. $0.0000180
         partial     P(yes) = 0.020  FAIL   350 ms   model=jev-1.13.0  tokens=444  est. $0.0000177
stage 4  one request, three shapes, about the fabricated case   jev-1.13.0   282 ms   593 in / 71 out
         noul   P(yes) = 0.010
         choice → high   confidence 1.000   { low 0.000, medium 0.000, high 1.000 }
         score  = 0 of 0..3   confidence 1.000   { 0: 1.000, 1: 0, 2: 0, 3: 0 }
stage 5  three evaluator kinds in one composite (GLM judge sees the ledger through the sample's ContextAwareJudge)
         grounded    composite 0.992 PASS   non_empty 1.000 [atomic-code]   grounded 0.980 [atomic-decision] jev-1.13.0   faithful_llm 1.000 [atomic-llm] GLM-5.3-Flash@bitdeer
         fabricated  composite 0.224 FAIL   non_empty 1.000                 grounded 0.010                                faithful_llm 0.050
         partial     composite 0.328 FAIL   non_empty 1.000                 grounded 0.020                                faithful_llm 0.300
```

Same three probabilities as on 2026-09-20 (0.97 / 0.01 / 0.02 then; 0.98 / 0.01 / 0.02 now), so the ≈0.01 noise
floor holds across a day and a release. The GLM judge, given the ledger, ranks the three cases in the same order
(1.00 / 0.05 / 0.30); it is more lenient on the partial case than Jev's strict "every claim" question, which is
the question's doing, not the model's. Three cases; still not calibration evidence — that is sample N3's job.

## Status against ADR-033 §7

| Requirement | State |
|---|---|
| (1) one real call per provider, evidence kept | **done for both**: TypeSafe 5 calls on 2026-09-20 + 7 on 2026-09-21 (3 `DecisionEval` calls in stages 2–3, 1 batch request in stage 4, 3 `grounded` leaves in stage 5); Bitdeer smoke 2026-09-20, then N1 step 3 (3 composites) and N2 stage 5 (3 GLM judge leaves) on 2026-09-21. |
| (2) shadow run on a labelled composite family | not started |
| (3) threshold on held-out data; escalation decision | not started |
