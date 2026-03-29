# ADR-007: Metrics Taxonomy and Categorization

## Status

Accepted

## Date

2026-01-15

## Context

AgentEval has grown to include 14+ metrics across different categories (RAG, Agentic, Embedding, Conversation). As we plan to add more metrics based on analysis of Azure AI Evaluation SDK and industry standards, we need a clear taxonomy to:

1. Help users discover and choose appropriate metrics
2. Guide implementation decisions for new metrics
3. Maintain consistency in naming and behavior
4. Support future extensibility (safety metrics, multimodal, etc.)

### Current State

Existing interface hierarchy:
```
IMetric
├── IRAGMetric (RequiresContext, RequiresGroundTruth)
└── IAgenticMetric (RequiresToolUsage)
```

Naming prefixes: `llm_`, `code_`, `embed_`

### Problem

1. **Flat categorization**: All quality metrics are either RAG or Agentic - no room for safety, fluency, coherence
2. **Missing metadata**: No programmatic way to query metric categories, costs, or requirements
3. **Limited discoverability**: Users must read documentation to understand what metrics are available

## Decision

### 1. Extend Interface Hierarchy (Optional Interfaces)

Add optional marker interfaces for additional capabilities:

```csharp
/// <summary>
/// Marker interface for quality evaluation metrics.
/// Quality metrics assess the substantive quality of agent responses.
/// </summary>
public interface IQualityMetric : IMetric { }

/// <summary>
/// Marker interface for safety evaluation metrics.
/// Safety metrics assess potential harms, toxicity, or policy violations.
/// </summary>
public interface ISafetyMetric : IMetric { }

/// <summary>
/// Marker interface for performance evaluation metrics.
/// Performance metrics assess efficiency, latency, and resource usage.
/// </summary>
public interface IPerformanceMetric : IMetric { }
```

### 2. Add MetricCategory Enumeration

```csharp
[Flags]
public enum MetricCategory
{
    None = 0,
    
    // Data requirements
    RequiresContext = 1 << 0,
    RequiresGroundTruth = 1 << 1,
    RequiresToolUsage = 1 << 2,
    RequiresEmbeddings = 1 << 3,
    
    // Evaluation domain
    RAG = 1 << 4,
    Agentic = 1 << 5,
    Conversation = 1 << 6,
    Safety = 1 << 7,
    
    // Quality aspects
    Faithfulness = 1 << 8,
    Relevance = 1 << 9,
    Coherence = 1 << 10,
    Fluency = 1 << 11,

    // Computation method
    LLMEvaluated = 1 << 12,  // renamed from LLMBased
    EmbeddingBased = 1 << 13,
    CodeBased = 1 << 14,

    // Domain extensions
    Memory = 1 << 15
}
```

### 3. Extend IMetric with Optional Metadata

```csharp
public interface IMetric
{
    string Name { get; }
    Task<MetricResult> EvaluateAsync(EvaluationContext context);
    
    // Optional metadata (default implementations)
    MetricCategory Categories => MetricCategory.None;
    string? Description => null;
    decimal? EstimatedCostPerEvaluation => null;
}
```

### 4. Metric Naming Convention (Confirmed)

Retain existing prefixes with formal definition:

| Prefix | Meaning | Categories Flag | Example |
|--------|---------|-----------------|---------|
| `llm_` | Requires LLM API call | `MetricCategory.LLMEvaluated` | `llm_faithfulness` |
| `code_` | Computed by code only | `MetricCategory.CodeBased` | `code_tool_success` |
| `embed_` | Requires embedding API | `MetricCategory.EmbeddingBased` | `embed_answer_similarity` |

### 5. Metric Discovery Service

```csharp
public interface IMetricRegistry
{
    IReadOnlyList<IMetric> GetAllMetrics();
    IReadOnlyList<IMetric> GetMetricsByCategory(MetricCategory category);
    IMetric? GetMetricByName(string name);
    void Register(IMetric metric);
}
```

## Rationale

### Why Flags Enum?

A flags enum allows combining multiple categories:
```csharp
Categories = MetricCategory.RAG | MetricCategory.RequiresContext | MetricCategory.LLMEvaluated
```

This is more flexible than a single category assignment.

### Why Optional Interfaces?

Marker interfaces like `IQualityMetric` and `ISafetyMetric`:
- Enable compile-time type safety for metric filtering
- Support DI registration patterns (`services.AddSingleton<ISafetyMetric, ToxicityMetric>()`)
- Allow grouping without breaking existing code

### Why Not Break Existing Interfaces?

The new categories and metadata are additive with default values. Existing metric implementations continue to work unchanged.

## Consequences

### Positive

1. **Better discoverability**: Users can query metrics by category
2. **Future-proof**: Safety, multimodal metrics fit naturally
3. **Tooling support**: CLI can list metrics by category
4. **Cost awareness**: Estimated costs are queryable

### Negative

1. **More code**: Additional interfaces and enum to maintain
2. **Optional complexity**: Developers must decide which interfaces to implement
3. **Migration effort**: Existing metrics should be updated with categories (non-breaking)

### Migration Path

1. Add new interfaces and enum (Phase 1)
2. Update existing metrics with Categories property (Phase 2)
3. Add IMetricRegistry service (Phase 3)
4. Update CLI to use categories (Phase 4)

## Alternatives Considered

### 1. Attribute-Based Categorization

```csharp
[MetricCategory(Category.RAG, Category.LLMEvaluated)]
public class FaithfulnessMetric : IRAGMetric { }
```

**Rejected**: Requires reflection, harder to query at runtime, doesn't support DI patterns.

### 2. Separate Category Hierarchy

```csharp
IMetric
├── IQualityMetric
│   ├── IRAGMetric
│   └── IAgenticMetric
└── ISafetyMetric
```

**Rejected**: Too rigid. Some metrics span categories (e.g., a safety metric that's also RAG).

### 3. No Formal Taxonomy

Keep flat structure, rely on documentation.

**Rejected**: Doesn't scale as metric count grows. Poor tooling support.

## Related

- [ADR-001: Metric Naming Prefixes](001-metric-naming-prefixes.md)
- [Metrics Reference](../metrics-reference.md)
- [Evaluation Guide](../evaluation-guide.md)

---

*Authors: AI-assisted planning, January 2026*
