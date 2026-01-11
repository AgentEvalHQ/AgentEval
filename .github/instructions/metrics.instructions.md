---
applyTo: "src/AgentEval/Metrics/**/*.cs"
description: Guidelines for implementing AgentEval metrics
---

# Metric Implementation Guidelines

## Metric Naming Prefixes
ALL metrics MUST use these prefixes in their `Name` property:
- `llm_` = LLM-evaluated (costs API calls) → `llm_faithfulness`
- `code_` = Computed by code (free) → `code_tool_success`
- `embed_` = Embedding-based (costs embedding API) → `embed_answer_similarity`

## Interface Selection
- `IRAGMetric` - For metrics that evaluate retrieval-augmented generation
  - Set `RequiresContext = true` if context documents needed
  - Set `RequiresGroundTruth = true` if expected answer needed
- `IAgenticMetric` - For metrics that evaluate agent tool usage
  - Set `RequiresToolUsage = true`

## Standard Metric Structure
```csharp
public class MyMetric : IRAGMetric
{
    private readonly IChatClient _evaluator;
    
    public string Name => "llm_my_metric";
    public string Description => "Evaluates XYZ quality";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => false;
    
    public MyMetric(IChatClient evaluator) => _evaluator = evaluator;
    
    public async Task<MetricResult> EvaluateAsync(
        EvaluationContext context, 
        CancellationToken ct = default)
    {
        // Validate required fields
        if (string.IsNullOrEmpty(context.Context))
            return MetricResult.Fail(Name, "Context is required");
        
        // Call LLM for evaluation
        var prompt = BuildEvaluationPrompt(context);
        var response = await _evaluator.GetResponseAsync([...], ct: ct);
        
        // Parse response (use LlmJsonParser for JSON responses)
        var parsed = LlmJsonParser.Parse<EvalResponse>(response.Text);
        
        return MetricResult.Pass(Name, parsed.Score, parsed.Explanation);
    }
}
```

## Using LlmJsonParser
For LLM responses that return JSON, use the built-in parser:
```csharp
var result = LlmJsonParser.Parse<T>(responseText);
// Handles markdown code blocks, extracts JSON, deserializes
```

## Score Normalization
All scores should be 0-100 scale. Use `ScoreNormalizer`:
```csharp
var normalized = ScoreNormalizer.From1To5(rawScore); // 1-5 → 0-100
var normalized = ScoreNormalizer.From0To1(rawScore); // 0-1 → 0-100
```

## File Location
- RAG metrics: `src/AgentEval/Metrics/RAG/`
- Agentic metrics: `src/AgentEval/Metrics/Agentic/`
- Embedding metrics: `src/AgentEval/Metrics/RAG/` (with `embed_` prefix)

## Required Tests
Add tests in `tests/AgentEval.Tests/Metrics/` using `FakeChatClient`:
```csharp
[Fact]
public async Task MyMetric_HighScore_WhenQualityGood()
{
    var fakeClient = new FakeChatClient("""{"score": 95}""");
    var metric = new MyMetric(fakeClient);
    var result = await metric.EvaluateAsync(context);
    Assert.True(result.Passed);
}
```
