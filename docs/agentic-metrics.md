# Agentic Metrics Guide

> **Comprehensive evaluation for AI agent tool usage and task completion**

---

## Overview

AgentEval provides **5 metrics** specifically designed for evaluating AI agents that use tools, execute workflows, and complete complex tasks.

### Metric Categories

| Category | Metrics | Cost | Best For |
|----------|---------|------|----------|
| **Code-based** | Tool Selection, Tool Arguments, Tool Success, Tool Efficiency | **FREE** | CI/CD, rapid iteration |
| **LLM-based** | Task Completion | $$$ | Semantic evaluation, complex tasks |

### When to Use Each

```
Agent Execution Stage       Recommended Metrics
---------------------------------------------------------
                           +-----------------------------+
  User Request             |                             |
    |                      |                             |
    v                      |                             |
+-----------+              |  code_tool_selection (FREE) |
|  Planning |--------------|  Did agent choose right     |
+-----------+              |  tools for the task?        |
    |                      |                             |
    v                      |                             |
+-----------+              |  code_tool_arguments (FREE) |
| Execution |--------------|  Were parameters correct?   |
+-----------+              |  code_tool_success (FREE)   |
    |                      |  Did calls succeed?         |
    v                      |                             |
+-----------+              |  code_tool_efficiency (FREE)|
|  Results  |--------------|  Optimal path taken?        |
+-----------+              |  llm_task_completion ($$$)  |
                           |  Was task fully completed?  |
                           +-----------------------------+
```

---

## Code-Based Metrics (FREE)

These metrics analyze tool call records directly—**no API calls required**.

### code_tool_selection

**Purpose:** Validates that the agent selected the appropriate tools for the task.

| Property | Value |
|----------|-------|
| Interface | `IAgenticMetric` |
| Requires | Tool Usage |
| Cost | **FREE** |
| Default Threshold | 100% (all expected tools called) |

**What It Checks:**
- Were all expected tools called?
- Were any forbidden tools called?
- Were tools called in the correct order?

**Example:**
```csharp
var metric = new ToolSelectionMetric(
    expectedTools: ["SearchDatabase", "FormatResults"],
    forbiddenTools: ["DeleteRecord"]);

var context = new EvaluationContext
{
    ToolCalls = new[]
    {
        new ToolCall { Name = "SearchDatabase", Success = true },
        new ToolCall { Name = "FormatResults", Success = true }
    }
};

var result = await metric.EvaluateAsync(context);
// Score: 100 (all expected tools called, no forbidden tools)
```

**AdditionalData:**
- `expected_tools` - List of tools that should be called
- `forbidden_tools` - List of tools that must not be called
- `called_tools` - List of tools actually called
- `missing_tools` - Expected tools that weren't called
- `violation_tools` - Forbidden tools that were called

---

### code_tool_arguments

**Purpose:** Validates that tool calls included correct arguments.

| Property | Value |
|----------|-------|
| Interface | `IAgenticMetric` |
| Requires | Tool Usage |
| Cost | **FREE** |
| Default Threshold | 100% (all required args present) |

**What It Checks:**
- All required arguments provided?
- Argument values match expected patterns?
- No invalid or unexpected arguments?

**Example:**
```csharp
var metric = new ToolArgumentsMetric(new Dictionary<string, string[]>
{
    ["SearchDatabase"] = ["query", "limit"],
    ["SendEmail"] = ["recipient", "subject", "body"]
});

var context = new EvaluationContext
{
    ToolCalls = new[]
    {
        new ToolCall 
        { 
            Name = "SearchDatabase", 
            Arguments = new { query = "sales Q3", limit = 10 }
        }
    }
};

var result = await metric.EvaluateAsync(context);
// Score: 100 (all required arguments present)
```

**AdditionalData:**
- `tools_checked` - Number of tool calls validated
- `missing_arguments` - Required args not provided
- `argument_errors` - Specific validation failures

---

### code_tool_success

**Purpose:** Measures the success rate of tool executions.

| Property | Value |
|----------|-------|
| Interface | `IAgenticMetric` |
| Requires | Tool Usage |
| Cost | **FREE** |
| Default Threshold | 100% (all tools succeed) |

**What It Checks:**
- Did all tool calls complete successfully?
- Error rates and patterns
- Retry success rates (if applicable)

**Example:**
```csharp
var metric = new ToolSuccessMetric();

var context = new EvaluationContext
{
    ToolCalls = new[]
    {
        new ToolCall { Name = "SearchDatabase", Success = true },
        new ToolCall { Name = "SendEmail", Success = true },
        new ToolCall { Name = "UpdateRecord", Success = false, Error = "Timeout" }
    }
};

var result = await metric.EvaluateAsync(context);
// Score: 67 (2 of 3 succeeded)
```

**AdditionalData:**
- `total_calls` - Total tool invocations
- `successful_calls` - Tools that succeeded
- `failed_calls` - Tools that failed
- `error_summary` - Breakdown of error types

---

### code_tool_efficiency

**Purpose:** Measures whether the agent took an optimal path to complete the task.

| Property | Value |
|----------|-------|
| Interface | `IAgenticMetric` |
| Requires | Tool Usage |
| Cost | **FREE** |
| Default Threshold | 80% |

**What It Checks:**
- Unnecessary tool calls?
- Redundant operations?
- Optimal ordering?

**Scoring:**
| Efficiency | Score | Description |
|------------|-------|-------------|
| Optimal | 100 | Minimum necessary calls |
| Good | 80-99 | Minor inefficiencies |
| Fair | 50-79 | Some redundant calls |
| Poor | <50 | Significant waste |

**Example:**
```csharp
var metric = new ToolEfficiencyMetric(optimalCallCount: 3);

var context = new EvaluationContext
{
    ToolCalls = new[]
    {
        new ToolCall { Name = "SearchDatabase", Success = true },
        new ToolCall { Name = "SearchDatabase", Success = true }, // Redundant
        new ToolCall { Name = "SearchDatabase", Success = true }, // Redundant
        new ToolCall { Name = "FormatResults", Success = true }
    }
};

var result = await metric.EvaluateAsync(context);
// Score: 75 (3 optimal / 4 actual = 75%)
```

**AdditionalData:**
- `optimal_calls` - Expected minimum calls
- `actual_calls` - Actual number of calls
- `redundant_calls` - Unnecessary calls identified
- `efficiency_ratio` - Optimal / Actual

---

## LLM-Based Metrics ($$$)

Semantic evaluation using LLM-as-judge for complex task assessment.

### llm_task_completion

**Purpose:** Evaluates whether the agent fully completed the requested task.

| Property | Value |
|----------|-------|
| Interface | `IAgenticMetric` |
| Requires | Input, Output |
| Cost | ~$0.01-0.05/eval |

**What It Evaluates:**
- Was the user's request fully addressed?
- Are there any incomplete aspects?
- Quality of task completion

**Scoring:**
| Completion | Score | Description |
|------------|-------|-------------|
| Full | 90-100 | Task completely done |
| Partial | 50-89 | Some aspects incomplete |
| Failed | 0-49 | Task not accomplished |

**Example:**
```csharp
var metric = new TaskCompletionMetric(chatClient);

var context = new EvaluationContext
{
    Input = "Find all customers in New York and send them a promotional email",
    Output = "Found 47 customers in New York. Email sent to all.",
    ToolCalls = new[]
    {
        new ToolCall { Name = "SearchCustomers", Success = true },
        new ToolCall { Name = "SendBulkEmail", Success = true }
    }
};

var result = await metric.EvaluateAsync(context);
// Score: 95 (task fully completed with confirmation)
```

**AdditionalData:**
- `completion_aspects` - Breakdown of task components
- `missing_aspects` - What wasn't completed
- `quality_notes` - LLM's quality assessment

---

## Fluent Assertions (Alternative API)

For more expressive test assertions, use the fluent API:

```csharp
// Tool usage assertions
result.ToolUsage!.Should()
    .HaveCalledTool("SearchDatabase", because: "searching is required")
        .BeforeTool("FormatResults")
        .WithArgument("query", "sales Q3")
    .And()
    .NotHaveCalledTool("DeleteRecord", because: "read-only operation")
    .And()
    .HaveNoErrors();

// Performance assertions
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveEstimatedCostUnder(0.10m);
```

### Behavioral Policies

Enforce rules across all tests:

```csharp
// Never allow certain tools
var policy = new NeverCallToolPolicy("DeleteRecord", "DropTable");

// Require confirmation before dangerous actions
var confirmPolicy = new MustConfirmBeforePolicy("SendEmail", "UpdateDatabase");

harness.AddPolicy(policy);
harness.AddPolicy(confirmPolicy);
```

---

## Complete Agentic Evaluation Example

```csharp
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Assertions;

// Setup
var chatClient = GetAzureOpenAIChatClient();

// Define all agentic metrics
var metrics = new IMetric[]
{
    // FREE - Code-based
    new ToolSelectionMetric(
        expectedTools: ["SearchDatabase", "FormatResults"],
        forbiddenTools: ["DeleteRecord"]),
    new ToolArgumentsMetric(requiredArgs),
    new ToolSuccessMetric(),
    new ToolEfficiencyMetric(optimalCallCount: 2),
    
    // $$$ - LLM-based
    new TaskCompletionMetric(chatClient)
};

// Prepare evaluation context
var context = new EvaluationContext
{
    Input = "Find sales data for Q3 and format as a report",
    Output = "Q3 Sales Report:\n- Total: $1.2M\n- Growth: 15%",
    ToolCalls = new[]
    {
        new ToolCall 
        { 
            Name = "SearchDatabase", 
            Arguments = new { query = "sales Q3", limit = 100 },
            Success = true,
            Duration = TimeSpan.FromMilliseconds(250)
        },
        new ToolCall 
        { 
            Name = "FormatResults", 
            Arguments = new { format = "report" },
            Success = true,
            Duration = TimeSpan.FromMilliseconds(50)
        }
    }
};

// Run all metrics
Console.WriteLine("Metric                    Score  Passed");
Console.WriteLine("-----------------------------------------");

foreach (var metric in metrics)
{
    var result = await metric.EvaluateAsync(context);
    var status = result.Passed ? "PASS" : "FAIL";
    Console.WriteLine($"{metric.Name,-25} {result.Score,5:F0}  {status}");
}
```

**Sample Output:**

```
Metric                    Score  Passed
-----------------------------------------
code_tool_selection         100  PASS
code_tool_arguments         100  PASS
code_tool_success           100  PASS
code_tool_efficiency        100  PASS
llm_task_completion          95  PASS
```

---

## Cost Optimization Strategy

### CI/CD Pipeline (FREE only)

```csharp
// Fast, free metrics for every commit
var ciMetrics = new IMetric[]
{
    new ToolSelectionMetric(expectedTools),
    new ToolSuccessMetric(),
    new ToolEfficiencyMetric(optimalCallCount: 3)
};
```

### Development (Mixed)

```csharp
// Add semantic evaluation for deeper testing
var devMetrics = ciMetrics.Concat(new IMetric[]
{
    new TaskCompletionMetric(chatClient)
});
```

### Production Sampling

```csharp
var sampleRate = 0.05;  // 5% of agent executions

if (Random.Shared.NextDouble() < sampleRate)
{
    await RunFullAgenticEvaluation(context);
}
```

---

## Data Requirements

| Metric | Input | Output | Tool Calls | Cost |
|--------|:-----:|:------:|:----------:|:----:|
| `code_tool_selection` | - | - | ✅ | Free |
| `code_tool_arguments` | - | - | ✅ | Free |
| `code_tool_success` | - | - | ✅ | Free |
| `code_tool_efficiency` | - | - | ✅ | Free |
| `llm_task_completion` | ✅ | ✅ | Optional | LLM |

---

## Integration with Tool Usage Tracking

AgentEval automatically captures tool calls when using MAF integration:

```csharp
// MAFEvaluationHarness captures all tool calls automatically
var harness = new MAFEvaluationHarness(agent);
var result = await harness.RunEvaluationAsync(testCase);

// Tool calls available in result
foreach (var tool in result.ToolUsage!.ToolCalls)
{
    Console.WriteLine($"{tool.Name}: {tool.Success} ({tool.Duration.TotalMs}ms)");
}
```

### Manual Tool Call Recording

For custom agents, record tool calls explicitly:

```csharp
var toolCalls = new List<ToolCall>();

// In your agent's tool execution
toolCalls.Add(new ToolCall
{
    Name = toolName,
    Arguments = args,
    Success = true,
    Result = result,
    Duration = stopwatch.Elapsed
});

// Pass to evaluation context
var context = new EvaluationContext
{
    ToolCalls = toolCalls.ToArray()
};
```

---

## See Also

- [Fluent Assertions](assertions.md) - `Should().HaveCalledTool()` API
- [RAG Metrics](rag-metrics.md) - Metrics for retrieval-augmented generation
- [Metrics Reference](metrics-reference.md) - Quick reference for all metrics
- [Evaluation Guide](evaluation-guide.md) - Choosing metrics for your use case
- [Naming Conventions](naming-conventions.md) - Metric naming standards

---

*Last updated: January 2026*
