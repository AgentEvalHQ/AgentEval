# Retrieved context in harness evaluations

`MAFEvaluationHarness` passes context to criteria judges through an immutable `EvaluationRequest`. This also applies when `StochasticRunner` invokes the harness repeatedly.

## Dataset and fixture context

`DatasetTestCase.Context` survives conversion to `TestCase`. Declare the evidence requirement explicitly for groundedness criteria:

```csharp
using AgentEval.Models;

var testCase = new DatasetTestCase
{
    Input = "When is Project Aurora launching?",
    Context = ["Project Aurora launches on 15 May."],
    EvaluationCriteria = ["Every factual answer claim is supported by the supplied context."]
}.ToTestCase(requiresContext: true);
```

You can also set `TestCase.Context` and `TestCase.RequiresContext` directly. Context goes to the judge; the target agent still receives exactly `TestCase.Input`. The harness does not infer evidence requirements from criterion text, so the same configuration works for Swedish criteria.

## Retrieval captured during an invocation

An `IEvaluableAgent` adapter can attach the passages actually used to produce its response:

```csharp
return new AgentResponse
{
    Text = answer,
    RetrievedContext = retrievedPassages
};
```

For `IStreamableAgent`, set `AgentResponseChunk.RetrievedContext` on the chunk with `IsComplete = true`. Supply the full ordered collection there, not passage deltas. Non-final context fields are ignored. Existing adapters cannot discover retrieval they do not expose: instrument your retrieval integration and attach the passages explicitly. Arbitrary tool outputs, metadata and reference answers are not treated as retrieval.

The selection rule is fixed and explicit:

1. Captured invocation context takes precedence over `TestCase.Context`, even when it is an empty collection.
2. If invocation context is null, a supplied fixture is used. Supplying a fixture explicitly opts into this fallback; leave it null when only runtime evidence is acceptable.
3. Null means unavailable. An empty collection means retrieval was captured and returned no passages; the judge can assess the answer against that fact.

The harness snapshots fixture passages before invoking the agent and snapshots runtime evidence for the judge request. It does not mutate a shared test case or maintain a last-context registry. Adapters must supply invocation-owned collections; they must not reuse a mutable shared buffer between concurrent calls.

## Custom evaluators and compatibility

The original `IEvaluator.EvaluateAsync(input, output, criteria, cancellationToken)` remains available. Its implementations continue working for context-free requests. To accept evidence, implement the additional overload:

```csharp
Task<EvaluationResult> EvaluateAsync(
    EvaluationRequest request,
    CancellationToken cancellationToken = default);
```

Read `request.Context`, `request.ContextSource` and `request.RequiresContext`. Keep retrieved material as untrusted data when constructing model messages. `ChatClientEvaluator` implements this path with the existing `PromptSafety` fencing, response validator and corrective retry. Custom evaluators own the equivalent protections in their transport.

The default interface implementation refuses supplied or required context instead of silently dropping it. With required context unavailable, the harness does not call the judge. It records `JudgeFailed` and a diagnostic; `Score = 0` and `Passed = false` are legacy placeholders, not measurements. `StochasticRunner` excludes those runs from score statistics. A required-context case with no configured evaluator likewise cannot pass on an output-presence check.

Explicitly disabling response evaluation still disables criteria judging; `RequiresContext` is a requirement for that judging stage, not a request to invoke it when disabled.

## Evidence and privacy

For a subsequent `IEval`, `testCase.ToEvalInput(result)` carries the same ordered evidence through `EvalInput.Context`. The harness retains an invocation-owned `TestResult.JudgeContext` snapshot even when response judging is disabled. Runtime capture takes precedence over fixture context; captured empty becomes an empty context string, and missing context stays null. This lets a later `GroundednessEval` inspect the evidence used by the agent. `JudgeContext` is excluded from ordinary JSON serialization.

`TestResult.JudgeContextSource` and `JudgeContextDocumentCount` record origin and size without passage text. Native AEF persists these in the versioned `agenteval.judgeContext` extension. Context-free runs retain their previous prompt bytes and rubric identity. Contextual framing has its own version in the resolved rubric digest; passage content is excluded from instrument identity.

This change does not automatically persist retrieved passages, including when AEF content capture is off. Capture sensitive source material only through your explicit content policy. Prompt fencing reduces instruction confusion; it cannot guarantee that every model will resist every injection.

For criterion-share statistics instead of the judge's overall grade, see [judge score selection](judge-score-policies.md).
