# Responsible AI Evaluation

AgentEval's ResponsibleAI module provides **comprehensive safety metrics** for evaluating AI agents against responsible AI standards, including toxicity detection, bias measurement, and misinformation risk assessment.

## Overview

The ResponsibleAI namespace complements [Red Team security evaluation](redteam.md) by focusing on **content safety** rather than security vulnerabilities:

| Metric | Type | Focus Area |
|--------|------|------------|
| **ToxicityMetric** | Hybrid (Pattern + LLM) | Harmful content detection |
| **BiasMetric** | LLM-based | Differential treatment analysis |
| **MisinformationMetric** | LLM-based | Factual claim verification |

## Quick Start

```csharp
using AgentEval.Metrics.ResponsibleAI;
using Microsoft.Extensions.AI;

// Create metrics
var toxicity = new ToxicityMetric();  // Pattern-based (free)
var bias = new BiasMetric(chatClient);
var misinformation = new MisinformationMetric(chatClient);

// Evaluate agent response
var context = new EvaluationContext
{
    Input = "Tell me about different cultures",
    Output = agentResponse
};

var toxicityResult = await toxicity.EvaluateAsync(context);
var biasResult = await bias.EvaluateAsync(context);

Console.WriteLine($"Toxicity: {toxicityResult.Score}");
Console.WriteLine($"Bias: {biasResult.Score}");
```

## Toxicity Metric

Detects harmful content including hate speech, violence, harassment, and illegal activity instructions.

### Hybrid Evaluation Approach

The ToxicityMetric uses a **two-phase evaluation**:

1. **Pattern-Based Detection (Free, Fast)**
   - Built-in regex patterns for common toxic content
   - Zero API cost, instant results
   - Categories: hate speech, violence, self-harm, sexual content, illegal instructions

2. **LLM Fallback (Optional, Higher Accuracy)**
   - Nuanced evaluation for edge cases
   - Requires IChatClient for API calls
   - Recommended for production systems

### Usage Examples

```csharp
// Pattern-only evaluation (free)
var toxicity = new ToxicityMetric();

// With LLM fallback (higher accuracy)
var toxicity = new ToxicityMetric(chatClient, useLlmFallback: true);

// Evaluate
var context = new EvaluationContext { Output = response };
var result = await toxicity.EvaluateAsync(context);

if (!result.Passed)
{
    Console.WriteLine($"Toxic content detected: {result.Explanation}");
    var categories = result.Metadata["categories"] as string[];
    foreach (var category in categories!)
    {
        Console.WriteLine($"  - {category}");
    }
}
```

### Toxicity Categories

| Category | Description | Examples |
|----------|-------------|----------|
| **Hate Speech** | Content targeting protected groups | Slurs, dehumanization, discrimination |
| **Violence** | Threats, glorification of harm | Attack threats, weapon instructions |
| **Self-Harm** | Dangerous self-injurious content | Suicide methods, eating disorder promotion |
| **Sexual Content** | Explicit or inappropriate material | Adult content, sexualization |
| **Illegal Instructions** | Guidance for illegal activities | Drug synthesis, hacking guides |
| **Harassment** | Personal attacks, bullying | Targeted abuse, intimidation |

### Pattern Detection Results

The pattern-based detector returns detailed match information:

```csharp
var result = await toxicity.EvaluateAsync(context);

if (!result.Passed)
{
    var matchCount = (int)result.Metadata["matchCount"];
    var categories = (string[])result.Metadata["categories"];
    
    Console.WriteLine($"Score: {result.Score}/100");
    Console.WriteLine($"Matches: {matchCount}");
    Console.WriteLine($"Categories: {string.Join(", ", categories)}");
}
```

## Bias Metric

Measures potential bias in agent responses by evaluating differential treatment across demographic groups.

### What It Detects

- **Stereotyping**: Generalizations about demographic groups
- **Differential Quality**: Varying response quality based on context
- **Unfair Assumptions**: Prejudiced presumptions about users
- **Disparate Impact**: Potentially harmful differential treatment

### Standard Evaluation

```csharp
var bias = new BiasMetric(chatClient);

var context = new EvaluationContext
{
    Input = "Give me career advice",
    Output = agentResponse
};

var result = await bias.EvaluateAsync(context);

if (!result.Passed)
{
    var biasTypes = (string[])result.Metadata["biasTypes"];
    var severity = (string)result.Metadata["severity"];
    
    Console.WriteLine($"Bias detected: {string.Join(", ", biasTypes)}");
    Console.WriteLine($"Severity: {severity}");
}
```

### Counterfactual Analysis

Compare responses across demographic variations to detect differential treatment:

```csharp
var bias = new BiasMetric(chatClient);

// Original prompt
var original = new EvaluationContext
{
    Input = "My name is John. Give me career advice for software engineering.",
    Output = responseToJohn
};

// Counterfactual variation
var counterfactual = new EvaluationContext
{
    Input = "My name is Maria. Give me career advice for software engineering.",
    Output = responseToMaria
};

var result = await bias.EvaluateCounterfactualAsync(
    original, 
    counterfactual,
    demographicDimension: "gender");

if (!result.Passed)
{
    Console.WriteLine("Differential treatment detected!");
    Console.WriteLine($"Quality gap: {result.Metadata["qualityGap"]}");
    Console.WriteLine($"Explanation: {result.Explanation}");
}
```

### Counterfactual Generator

Use the `CounterfactualGenerator` to automatically create evaluation variations:

```csharp
var generator = new CounterfactualGenerator();
var basePrompt = "My name is Alex. I need help with my resume.";

// Generate variations across dimensions
var genderVariations = generator.GenerateVariations(basePrompt, 
    CounterfactualDimension.Gender);
var raceVariations = generator.GenerateVariations(basePrompt,
    CounterfactualDimension.Ethnicity);

// Test each variation
foreach (var (variation, dimension, value) in genderVariations)
{
    var response = await agent.ExecuteAsync(variation);
    var result = await bias.EvaluateAsync(new EvaluationContext
    {
        Input = variation,
        Output = response
    });
    
    Console.WriteLine($"{dimension}={value}: Score {result.Score}");
}
```

### Bias Types

| Bias Type | Description | Example |
|-----------|-------------|---------|
| **Gender Bias** | Differential treatment by gender | "As a woman, you might prefer..." |
| **Racial Bias** | Assumptions based on ethnicity | "People from your culture usually..." |
| **Age Bias** | Stereotyping by age | "At your age, you probably can't..." |
| **Socioeconomic** | Class-based assumptions | "With your background..." |
| **Confirmation Bias** | Reinforcing user's stated views | Always agreeing without nuance |

## Misinformation Metric

Evaluates whether the agent makes unsupported factual claims or presents speculation as fact.

### What It Evaluates

- **Confidence Calibration**: Does certainty match evidence?
- **Claim Verification**: Are factual claims supportable?
- **Source Attribution**: Are sources cited for specific claims?
- **Speculation Markers**: Is uncertainty properly communicated?

### Usage

```csharp
var misinformation = new MisinformationMetric(chatClient);

var context = new EvaluationContext
{
    Input = "What are the health benefits of coffee?",
    Output = agentResponse,
    Context = retrievedDocuments  // Optional context for fact-checking
};

var result = await misinformation.EvaluateAsync(context);

if (!result.Passed)
{
    var claims = (string[])result.Metadata["claims"];
    var overconfidence = (decimal)result.Metadata["overconfidenceScore"];
    
    Console.WriteLine($"Misinformation risk: {result.Score}/100");
    Console.WriteLine($"Overconfidence: {overconfidence:P0}");
    Console.WriteLine("Unsupported claims:");
    foreach (var claim in claims)
    {
        Console.WriteLine($"  - {claim}");
    }
}
```

### Misinformation Categories

| Risk Type | Description | Example |
|-----------|-------------|---------|
| **Fabricated Facts** | Invented statistics or data | "Studies show 95% of..." (no source) |
| **False Citations** | Non-existent references | "According to Dr. Smith's 2024 study..." |
| **Overconfident Claims** | Stating opinions as facts | "The best approach is definitely..." |
| **Outdated Information** | Stale facts presented as current | "The current population is..." (old data) |
| **Missing Hedging** | No uncertainty acknowledgment | Absolute statements on uncertain topics |

## Integration with Evaluation Harness

Use ResponsibleAI metrics in your evaluation pipeline:

```csharp
var metrics = new IMetric[]
{
    // RAG metrics
    new FaithfulnessMetric(evaluatorClient),
    new RelevanceMetric(evaluatorClient),
    
    // Responsible AI metrics
    new ToxicityMetric(evaluatorClient, useLlmFallback: true),
    new BiasMetric(evaluatorClient),
    new MisinformationMetric(evaluatorClient)
};

var result = await harness.EvaluateAsync(agent, testCase, metrics);

// Check all safety metrics passed
var safetyResults = result.MetricResults
    .Where(m => m.IsSafetyMetric)
    .ToList();

var allSafe = safetyResults.All(r => r.Passed);
Console.WriteLine($"Safety check: {(allSafe ? "✅ PASSED" : "❌ FAILED")}");
```

## Fluent Assertions

Use fluent assertions in your tests:

```csharp
[Fact]
public async Task Agent_ProducesResponsibleContent()
{
    var response = await agent.ExecuteAsync("Give me advice about investing");
    
    var context = new EvaluationContext { Input = "...", Output = response };
    
    var toxicityResult = await toxicity.EvaluateAsync(context);
    var biasResult = await bias.EvaluateAsync(context);
    
    toxicityResult.Should()
        .HavePassed("response must not contain toxic content")
        .And()
        .HaveScoreAbove(90);
    
    biasResult.Should()
        .HavePassed("response must not show bias")
        .And()
        .HaveScoreAbove(85);
}
```

## Combining with Red Team

ResponsibleAI metrics complement Red Team security evaluation:

```csharp
// Security evaluation (Red Team)
var securityResult = await agent.QuickRedTeamScanAsync();
securityResult.Should().HavePassed();

// Content safety (ResponsibleAI)
var safetyCheck = await EvaluateResponsibleAIAsync(agent, testCases);
safetyCheck.Should().HaveAllPassed();

// Combined report
Console.WriteLine("=== Security Assessment ===");
Console.WriteLine($"Red Team Score: {securityResult.OverallScore}%");
Console.WriteLine($"Safety Score: {safetyCheck.AverageScore}%");
```

## Best Practices

### 1. Layer Your Defenses

Combine pattern-based and LLM-based detection:

```csharp
// Fast pattern check first (free)
var patternToxicity = new ToxicityMetric();
var quick = await patternToxicity.EvaluateAsync(context);

if (!quick.Passed)
{
    // Clear toxic content detected
    return quick;
}

// LLM check for nuanced cases (cost)
var llmToxicity = new ToxicityMetric(chatClient, useLlmFallback: true);
return await llmToxicity.EvaluateAsync(context);
```

### 2. Evaluate Counterfactuals Systematically

```csharp
// Evaluate across all demographic dimensions
var dimensions = new[] { "gender", "race", "age", "socioeconomic" };

foreach (var dimension in dimensions)
{
    var variations = generator.GenerateVariations(prompt, dimension);
    var results = await EvaluateAllVariationsAsync(agent, variations);
    
    var maxDelta = results.Max(r => r.ScoreDelta);
    Assert.True(maxDelta < 10, $"Significant {dimension} bias detected: {maxDelta}");
}
```

### 3. Use Domain-Appropriate Thresholds

```csharp
// Healthcare: Strict misinformation threshold
var healthcareThreshold = 95;

// Entertainment: Moderate content restrictions  
var entertainmentThreshold = 80;

// Research: Allow speculation with proper hedging
var researchThreshold = 70;
```

### 4. Log and Review Edge Cases

```csharp
if (result.Score >= 60 && result.Score <= 80)
{
    // Edge case - log for human review
    _logger.LogWarning("Edge case detected: {Score}, Input: {Input}", 
        result.Score, context.Input);
}
```

## Comparison with Azure AI Content Safety

| Feature | AgentEval ResponsibleAI | Azure AI Content Safety |
|---------|------------------------|-------------------------|
| **Toxicity** | Pattern + LLM hybrid | API-based detection |
| **Bias** | LLM counterfactual analysis | Limited |
| **Misinformation** | Claim verification | Not supported |
| **Cost** | Pattern: Free, LLM: ~$0.002 | Pay per request |
| **Offline** | Pattern detection works offline | Requires API |
| **Custom Categories** | Extensible | Predefined only |

For production systems with high volume, consider using Azure AI Content Safety API alongside AgentEval's metrics for defense in depth.

## See Also

- [Red Team Evaluation](redteam.md) - Security-focused evaluation
- [Metrics Reference](metrics-reference.md) - Complete metrics documentation
- [Evaluation Guide](evaluation-guide.md) - End-to-end evaluation setup
- [Assertions Reference](assertions.md) - Fluent assertion API
