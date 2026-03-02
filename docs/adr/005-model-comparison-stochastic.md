# ADR-005: Model Comparison and stochastic evaluation Architecture

> **Status:** Accepted  
> **Date:** January 8, 2026  
> **Decision Makers:** AgentEval Core Team

---

## Context

AgentEval needs to support two closely related features:

1. **Stochastic Pass Criteria** — Run evaluations multiple times to handle LLM non-determinism and report statistical results (pass rate, confidence intervals, latency percentiles)

2. **Model Comparison** — Run the same evaluation suite across multiple LLM models (e.g., GPT-4o, Claude 3.5, GPT-4o-mini) and recommend the best model based on accuracy, latency, and cost

These features are tightly coupled because:
- Model comparison requires running evaluations multiple times per model (stochastic)
- Both features share infrastructure for parallel execution and result aggregation
- Statistical significance testing between models requires stochastic run data

### The Model Swapping Challenge

Current agent creation in Microsoft Agent Framework (MAF) tightly binds the model:

```csharp
var chatClient = azureClient.GetChatClient("gpt-4o").AsIChatClient();
var agent = new ChatClientAgent(chatClient, options);
```

The model deployment cannot be changed after the agent is created. We need a pattern to create agents with different models while preserving the same behavior (instructions, tools, configuration).

---

## Decision

### Primary Decision: Agent Factory Pattern

We will implement the **Agent Factory Pattern** where `IAgentFactory` creates fresh agent instances with specific model configurations.

```csharp
public interface IAgentFactory
{
    string ModelId { get; }
    string ModelName { get; }
    IEvaluableAgent CreateAgent();
    ModelConfiguration? Configuration { get; }
}
```

### Secondary Decision: Additive Interface for Model Identification

To avoid breaking existing `IEvaluableAgent` implementations, we introduce a separate optional interface:

```csharp
public interface IModelIdentifiable
{
    string? ModelId { get; }
    string? ModelName { get; }
}
```

Adapters that know their model can implement this interface. Existing implementations continue to work unchanged.

### Tertiary Decision: Unified Stochastic/Comparison Architecture

stochastic evaluation and model comparison share the same result aggregation infrastructure:

```
Test Cases → Stochastic Runner → Statistical Aggregation → Results
                   ↑
            (per model via factory)
                   ↑
           Model Comparer orchestrates
```

---

## Alternatives Considered

### Alternative 1: IChatClient Injection/Swapping

**Approach:** Inject different `IChatClient` instances into the same agent structure at runtime.

**Pros:**
- Single agent instance, swap the underlying client
- More memory efficient

**Cons:**
- Requires MAF-specific knowledge of agent internals
- Not all agent frameworks support client swapping
- Breaks encapsulation
- Would require changes to `ChatClientAgent` or reflection

**Decision:** Rejected — Too tightly coupled to MAF internals.

### Alternative 2: Reflection-Based Model Swapping

**Approach:** Use reflection to modify the deployment name in the existing chat client.

**Pros:**
- No new interfaces needed
- Works with existing agents

**Cons:**
- Fragile — depends on internal implementation details
- Will break when MAF updates
- Not type-safe
- Hard to test

**Decision:** Rejected — Too fragile and unmaintainable.

### Alternative 3: Configuration-Based Model Selection

**Approach:** Pass model configuration via environment variables or config files that the agent reads.

**Pros:**
- Simple to implement
- No code changes for users

**Cons:**
- Less type-safe
- Harder to test programmatically
- Requires re-initialization per model
- Can't compare models in a single process run

**Decision:** Rejected — Not suitable for programmatic comparison.

### Alternative 4: Extend IEvaluableAgent with ModelId

**Approach:** Add `string? ModelId` property directly to `IEvaluableAgent`.

**Pros:**
- Single interface
- All agents must report their model

**Cons:**
- Breaking change for all existing `IEvaluableAgent` implementations
- Many agents don't know their model (generic adapters)
- Violates Interface Segregation Principle

**Decision:** Rejected — Breaking change, ISP violation.

---

## Consequences

### Positive

1. **Framework Agnostic** — Factory pattern works with any agent framework, not just MAF
2. **Non-Breaking** — Existing `IEvaluableAgent` implementations continue to work
3. **Testable** — Easy to create mock factories for unit testing
4. **Clear Separation** — Agent logic is separated from model configuration
5. **Extensible** — New providers (Anthropic, Google) just need new factory implementations
6. **Composable** — Stochastic runner works standalone or within model comparer

### Negative

1. **Factory per Provider** — Each cloud provider needs its own factory implementation
2. **Learning Curve** — Users must understand factory pattern for model comparison
3. **More Types** — Additional interfaces and classes to maintain

### Neutral

1. **Optional Feature** — Simple single-model evaluation doesn't require factories
2. **MAF-Specific Factory** — We provide `AzureOpenAIAgentFactory` for common case

---

## Implementation Notes

### File Structure

```
src/AgentEval/
├── Comparison/                    # New folder
│   ├── IAgentFactory.cs
│   ├── IStochasticRunner.cs
│   ├── IModelComparer.cs
│   └── ... (implementations)
├── Core/
│   └── IModelIdentifiable.cs      # New interface
└── MAF/
    └── AzureOpenAIAgentFactory.cs # MAF-specific factory
```

### Key Interfaces

```csharp
// Factory creates agents with specific model
public interface IAgentFactory
{
    string ModelId { get; }
    string ModelName { get; }
    IEvaluableAgent CreateAgent();
}

// Optional: Agents can report their model
public interface IModelIdentifiable
{
    string? ModelId { get; }
    string? ModelName { get; }
}

// Stochastic runner handles multiple runs
public interface IStochasticRunner
{
    Task<StochasticResult> RunAsync(
        IEvaluableAgent agent,
        TestCase testCase,
        StochasticOptions? options = null);
}

// Model comparer orchestrates cross-model comparison
public interface IModelComparer
{
    IModelComparer AddModel(IAgentFactory factory);
    Task<ModelComparisonResult> CompareAsync(
        IEnumerable<TestCase> testCases,
        ModelComparisonOptions? options = null);
}
```

---

## Validation

This decision will be validated by:

1. **Unit tests** — Factory pattern produces correct agents
2. **Integration tests** — Stochastic runner aggregates results correctly
3. **Sample code** — Sample14 (Stochastic) and Sample15 (Model Comparison) demonstrate usage
4. **User feedback** — Monitor GitHub issues for usability concerns

---

## Related Documents

- [Model Comparison Guide](../model-comparison.md)
- [stochastic evaluation Guide](../stochastic-evaluation.md)
- [Sample14: stochastic evaluation](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample14_StochasticEvaluation.cs)
- [Sample15: Model Comparison](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample15_ModelComparison.cs)

---

*ADR maintained by AgentEval team. Status changes require team review.*
