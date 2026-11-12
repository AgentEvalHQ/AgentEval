# Groundedness policies

Grounding asks whether an answer's claims are supported by its evidence. Answer completeness asks whether it resolves the question. A short correct answer can satisfy both without repeating every fact in a retrieved document.

Use the support-focused policy for that distinction:

```csharp
var evaluation = new GroundednessEval(
    judge,
    judgeModel: "my-judge-deployment",
    passThreshold: 0.75,
    options: GroundednessOptions.SupportFocused);

var result = await evaluation.EvaluateAsync(new EvalInput(
    Query: "When does Aurora launch?",
    Response: "15 May.",
    Context: "Aurora launches on 15 May. Its office has five rooms."));
```

This versioned policy evaluates claim support, contradictions and citation accuracy with weights 0.50, 0.30 and 0.20. It does not invoke a source-coverage check. Missing (null) context makes the active checks unmeasured before calling the judge; it does not become a low grounding score or a passing result. An empty context string is captured-empty evidence and is judged as such.

The existing three-argument constructor retains the legacy v1 policy, including its `evidence_coverage` dimension. Existing callers and historical results keep that interpretation. The explicit support-focused policy reports evaluator version `2.0.0` and uses `agenteval.groundedness.v2`; these results should be calibrated and compared as a different instrument.

## Configuring a policy

```csharp
var policy = GroundednessOptions.SupportFocused with
{
    RequireCitations = true,
    ClaimSupport = new(Weight: 0.60),
    ClaimContradicted = new(Weight: 0.25),
    CitationAccuracy = new(Weight: 0.15),
    Completeness = new(Weight: 0.25, Enabled: true,
        OnFailure: ComponentFailureEffect.Warn)
};
```

Completeness in v2 measures what the question requires, not the fraction of all source facts reproduced. It is optional and independently named `answer_completeness`. Citation absence passes the citation dimension only when citations are optional; requiring citations changes the actual judge criteria. `RequireCitations` requires the enabled citation dimension and the v2 policy; the v1 rubric explicitly permits absent citations.

Each dimension declares `Enabled`, `Weight`, `Required` and `OnFailure`. These settings have different purposes:

- `Enabled = false` removes the check and its judge call entirely.
- A zero weight leaves an enabled check's requiredness and failure effect active. Use `Enabled` to disable it.
- `Required` prevents a missing measurement from being silently excluded to produce a pass.
- `OnFailure` determines whether a measured failure fails the composite, warns, or affects only the weighted score.

Weights must be finite and nonnegative, and active weights must have a positive finite total. The aggregate normalizes the measured dimensions' weights; every required check must still be measured for a pass. The options are immutable values owned by the evaluation; no customer configuration is registered in a process-wide table.

For harness-based criteria evaluation, retrieval must also reach the judge. See [passing judge context](judge-context.md). These changes provide configurable scoring and deterministic wiring tests; they do not establish semantic accuracy for every language or dataset. Calibrate a changed policy against reviewed examples before treating its scores as equivalent to another instrument.

Thanks to [@adner](https://github.com/adner) (Andreas Adner) for identifying the short-answer/source-coverage mismatch.
