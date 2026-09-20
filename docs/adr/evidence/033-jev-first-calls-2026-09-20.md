# ADR-033 evidence — first real calls, 2026-09-20

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

## Status against ADR-033 §7

| Requirement | State |
|---|---|
| (1) one real call per provider, evidence kept | TypeSafe: **done** (5 calls). Bitdeer: **one call done**, judged run blocked by 402. |
| (2) shadow run on a labelled composite family | not started |
| (3) threshold on held-out data; escalation decision | not started |
