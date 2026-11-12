# Text-token cost estimation

AgentEval estimates text-token costs from reported usage and a rate card. This is not invoice reconciliation: cache writes, storage, tools, image/audio charges, taxes, regional tiers and negotiated billing rules may need a provider-specific calculation.

For `MAFEvaluationHarness`, model selection is:

1. `EvaluationOptions.ModelName`, when supplied (a blank value still disables ordinary model lookup).
2. A nonblank `AgentResponse.ModelId`, or the final streaming chunk's model ID.
3. The agent's configured `IModelIdentifiable.ModelId`.

An unknown returned ID stays unpriced; it does not fall back to a different known model. The `response` provenance label identifies the field used, not proof that an upstream SDK independently observed that ID. A deployment alias needs an explicit rate or model override.

Responses containing multiple model identities without per-model attribution stay unpriced (`ModelSource = "mixed"`), even when usage is missing or invalid. `HasMixedModelUsage` survives trace recording and replay independently of token counts. Pricing requires an explicit model override; a rate resolver alone cannot remove this ambiguity.

## Cached input

Provider `CachedInputTokenCount` is a **subset** of input tokens. With all counts and rates known:

```text
cost = (input - cached) × input_rate + cached × cached_rate + output × output_rate
```

Rates below use USD per thousand tokens. Input 10,000, cached input 8,000 and output 1,000 at rates $0.001 / $0.0001 / $0.002 produce **$0.0048**, rather than the undiscounted $0.012.

```csharp
var options = new EvaluationOptions
{
    PricingResolver = model => model == "customer-deployment"
        ? new TokenPricing(0.001m, 0.002m, 0.0001m) { Source = "customer-rate-card-v1" }
        : null
};
```

This resolver belongs to the evaluation options. It does not mutate a shared registry. A supplied resolver returning `null` means unpriced, with no global fallback. The existing global registration methods remain available for application-wide defaults; two-rate registration clears any previous cached rate for that key.

For a judge, the existing `AtomicLlmEval` instance `rateResolver` accepts an extended rate value:

```csharp
rateResolver: model => new JudgeCostMap.ModelRate(0.001, 0.002)
{
    CachedInputRatePer1K = 0.0001,
    Source = "customer-rate-card-v1"
}
```

`ChatClientEvaluator` includes usage from the first attempt and corrective retry. Missing usage on an attempt cannot be repaired by a later attempt. Provider prompt caching and `EvalProvenance.CacheHit` are different: the latter means a cached evaluation result.

## Reading the estimate

`PerformanceMetrics.CostEstimate` and leaf `EvalProvenance.CostEstimate` retain the amount, counts, effective rates, source and status. The scalar cost fields remain compatible. The native harness AEF writer stores the details in `ext["agenteval.costEstimate"]`; readback does not look up today's price.

Native composite output preserves leaf cost estimates in the same extension. AEF usage `costSource` is a bounded summary: the CLI records cache assumptions and frozen rates, or a rate-card digest for unusually long decimal representations. Detailed rate-card source text stays in cost provenance rather than overflowing the summary field. Provider input caching is distinct from a cached evaluation result.

| Status | Meaning |
|---|---|
| `CacheAdjusted` | Known cached subset charged at the configured cached rate. |
| `Standard` | Provider explicitly reported zero cached tokens. |
| `UndiscountedUnknownCache` | Cache count absent; ordinary input price used. |
| `UndiscountedMissingCacheRate` | Cache count known, cached price absent; ordinary input price used. |
| `ZeroUsage` | Both token components explicitly zero. |
| `Unpriced` | Required usage or rate unavailable. |
| `Invalid` | Negative/inconsistent counts, invalid rates or arithmetic overflow; no price produced. |

An undiscounted estimate is not a measured bill or necessarily a lower bound. Missing values are not zero. Invalid cached counts (for example cached input greater than input) are not silently clamped. Lossless provider counts are kept for pricing even where legacy integer display fields saturate. Text-length fallback usage remains explicitly marked `UsageEstimated`.

Performance benchmarks capture their rate table before execution, price each response, and retain `CostEstimates`. A known explicit model key overrides response identities; otherwise each reported model is resolved separately. Any unpriced response or failed call prevents a complete workload total. Partial-budget checks use a conservative lower bound rather than claiming an undiscounted subtotal is a minimum bill.

## Bundled cached rates

Cached rates for GPT-4o, GPT-4o Mini and GPT-4.1 were checked on 2026-11-11 against the official model pages: [GPT-4o](https://developers.openai.com/api/docs/models/gpt-4o), [GPT-4o Mini](https://developers.openai.com/api/docs/models/gpt-4o-mini), [GPT-4.1](https://developers.openai.com/api/docs/models/gpt-4.1). These are standard text list rates, not a quote for every hosting provider or service tier. Other models retain unknown cached prices until explicitly configured; no universal discount is assumed.

Thanks to [@adner](https://github.com/adner) (Andreas Adner) for reporting the model-identity gap and the importance of cached-input discounts.
