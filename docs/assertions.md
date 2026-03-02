# Fluent Assertions Guide

AgentEval provides expressive fluent assertions inspired by [FluentAssertions](https://fluentassertions.com/). These assertions provide rich failure messages with context, suggestions, and structured output to make debugging test failures fast and intuitive.

## Overview

AgentEval offers three categories of fluent assertions:

| Category | Purpose | Entry Point |
|----------|---------|-------------|
| **Tool Assertions** | Verify tool/function calls | `result.ToolUsage!.Should()` |
| **Performance Assertions** | Check latency, tokens, cost | `result.Performance!.Should()` |
| **Response Assertions** | Validate response content | `result.ActualOutput!.Should()` |

## Key Features

### Rich Failure Messages

When an assertion fails, you get structured output with:

- **Expected vs Actual** values clearly displayed
- **Context** showing relevant state (tool timeline, response preview)
- **Suggestions** for common fixes
- **"Because" reasons** you provide for documentation

**Example failure output:**

```
Expected tool 'SearchTool' to be called, but it was not because the query requires web search.

Expected: Tool 'SearchTool' called at least once
Actual:   Tools called: [CalculateTool, FormatTool]

Tools called:
  • CalculateTool
  • FormatTool

Suggestions:
  → Verify the agent has access to the expected tools
  → Check if the prompt clearly requests tool usage
```

### The "Because" Parameter

All assertions accept an optional `because` parameter to document *why* the assertion matters:

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SecurityScanner", because: "user data must be validated before processing")
    .HaveNoErrors(because: "failed security scans should block the pipeline");
```

### Assertion Scopes

Use `AgentEvalScope` to collect multiple failures before throwing, similar to FluentAssertions' `AssertionScope`:

```csharp
using (new AgentEvalScope())
{
    result.ToolUsage!.Should().HaveCalledTool("SearchTool");
    result.ToolUsage!.Should().HaveCalledTool("CalculateTool");
    result.Performance!.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
    result.ActualOutput!.Should().Contain("result");
}
// Throws single exception listing ALL failures
```

**Scope failure output:**

```
Multiple assertion failures occurred (3 total):
────────────────────────────────────────────────────────────────

Failure 1:
  Expected tool 'SearchTool' to be called, but it was not.
  ...

────────────────────────────────────────

Failure 2:
  Expected tool 'CalculateTool' to be called, but it was not.
  ...

────────────────────────────────────────

Failure 3:
  Expected total duration to be under the specified maximum.
  ...
```

## Behavioral Policy Assertions

Behavioral Policy Assertions are safety-critical assertions that enforce behavioral constraints on AI agent actions. They provide "guardrails as code" — hard pass/fail constraints that prevent agents from taking dangerous, unauthorized, or policy-violating actions.

### NeverCallTool

Assert that a forbidden tool was never called:

```csharp
// Block dangerous tools
result.ToolUsage!.Should()
    .NeverCallTool("DeleteDatabase", 
        because: "production data must never be deleted by agents")
    .NeverCallTool("ExecuteTrade",
        because: "trades require human approval");
```

### NeverPassArgumentMatching

Detect forbidden patterns (PII, secrets) in tool arguments using regex:

```csharp
// Detect SSN patterns in any tool argument
result.ToolUsage!.Should()
    .NeverPassArgumentMatching(@"\b\d{3}-\d{2}-\d{4}\b",
        because: "SSNs must never be passed to external tools");

// Detect email addresses  
result.ToolUsage!.Should()
    .NeverPassArgumentMatching(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Z|a-z]{2,}\b",
        because: "email addresses are PII and must be anonymized");

// With regex options
result.ToolUsage!.Should()
    .NeverPassArgumentMatching("password|secret|api_key",
        because: "credentials must never appear in arguments",
        regexOptions: RegexOptions.IgnoreCase);
```

**Automatic Redaction:** When a match is found, sensitive data is automatically redacted in the exception message (e.g., `1***9` for SSN `123-45-6789`).

### MustConfirmBefore

Require a confirmation step before risky actions:

```csharp
// Require confirmation before destructive actions
result.ToolUsage!.Should()
    .MustConfirmBefore("TransferFunds",
        because: "financial transfers require explicit user consent",
        confirmationToolName: "GetUserConfirmation");

// Default confirmation tool is "get_confirmation" or "confirm"
result.ToolUsage!.Should()
    .MustConfirmBefore("DeleteUser",
        because: "user deletion is irreversible");
```

### BehavioralPolicyViolationException

When a policy is violated, a structured exception provides rich diagnostics:

```csharp
try
{
    result.ToolUsage!.Should()
        .NeverCallTool("DangerousTool", because: "safety requirement");
}
catch (BehavioralPolicyViolationException ex)
{
    Console.WriteLine($"Policy: {ex.PolicyName}");           // "NeverCallTool(DangerousTool)"
    Console.WriteLine($"Violation: {ex.ViolationType}");     // "ForbiddenTool"
    Console.WriteLine($"Action: {ex.ViolatingAction}");      // "Called DangerousTool 2 time(s)"
    Console.WriteLine($"Because: {ex.Because}");             // "safety requirement"
    
    foreach (var suggestion in ex.Suggestions ?? [])
    {
        Console.WriteLine($"  → {suggestion}");
    }
}
```

### Compliance Testing Patterns

Common patterns for regulatory compliance:

```csharp
// GDPR - Data Protection
result.ToolUsage!.Should()
    .MustConfirmBefore("ProcessPersonalData", 
        because: "GDPR requires explicit consent",
        confirmationToolName: "check_consent");

// HIPAA - Healthcare
result.ToolUsage!.Should()
    .NeverCallTool("export_raw_patient_data",
        because: "HIPAA prohibits unencrypted PHI export")
    .NeverPassArgumentMatching(@"\b\d{3}-\d{2}-\d{4}\b",
        because: "SSNs are PHI under HIPAA");

// PCI-DSS - Payment
result.ToolUsage!.Should()
    .NeverPassArgumentMatching(@"\b\d{16}\b",
        because: "raw card numbers violate PCI-DSS");

// SOX - Financial
result.ToolUsage!.Should()
    .MustConfirmBefore("ApproveExpense",
        because: "SOX requires dual approval for expenses",
        confirmationToolName: "GetManagerApproval");
```

---

## Tool Assertions

### Basic Tool Verification

```csharp
// Assert a tool was called
result.ToolUsage!.Should()
    .HaveCalledTool("get_weather");

// Assert a tool was NOT called
result.ToolUsage!.Should()
    .NotHaveCalledTool("delete_database");

// Assert at least one tool was called
result.ToolUsage!.Should()
    .HaveCalledAnyTool();
```

### Call Count Assertions

```csharp
// Exact count
result.ToolUsage!.Should()
    .HaveCallCount(3);

// Minimum count
result.ToolUsage!.Should()
    .HaveCallCountAtLeast(2);

// Specific tool call count
result.ToolUsage!.Should()
    .HaveCalledTool("retry_operation")
    .Times(3);
```

### Call Order Assertions

```csharp
// Assert tools called in specific order
result.ToolUsage!.Should()
    .HaveCallOrder("authenticate", "fetch_data", "format_output");

// Chain order assertions
result.ToolUsage!.Should()
    .HaveCalledTool("authenticate")
        .BeforeTool("fetch_data")
    .And()
    .HaveCalledTool("validate")
        .AfterTool("fetch_data");
```

### Argument Assertions

```csharp
// Exact argument match
result.ToolUsage!.Should()
    .HaveCalledTool("search")
        .WithArgument("query", "weather forecast");

// Argument contains substring
result.ToolUsage!.Should()
    .HaveCalledTool("search")
        .WithArgumentContaining("location", "Seattle");
```

### Result Assertions

```csharp
// Assert tool result contains text
result.ToolUsage!.Should()
    .HaveCalledTool("fetch_data")
        .WithResultContaining("success");

// Assert tool completed without error
result.ToolUsage!.Should()
    .HaveCalledTool("process")
        .WithoutError();

// Assert no tools had errors
result.ToolUsage!.Should()
    .HaveNoErrors();
```

### Duration Assertions

```csharp
// Assert tool completed quickly
result.ToolUsage!.Should()
    .HaveCalledTool("cache_lookup")
        .WithDurationUnder(TimeSpan.FromMilliseconds(100));
```

### Fluent Chaining

Chain multiple assertions fluently:

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchTool")
        .BeforeTool("ProcessTool")
        .WithArgument("query", "test")
        .WithoutError()
    .And()
    .HaveCalledTool("ProcessTool")
        .AfterTool("SearchTool")
        .WithDurationUnder(TimeSpan.FromSeconds(2))
    .And()
    .HaveNoErrors()
    .HaveCallCount(2);
```

## Performance Assertions

### Duration Assertions

```csharp
// Total request duration
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5));

// Time to first token (streaming)
result.Performance!.Should()
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500));

// Minimum duration (for rate limiting tests)
result.Performance!.Should()
    .HaveTotalDurationAtLeast(TimeSpan.FromSeconds(1));
```

### Token Assertions

```csharp
// Total tokens
result.Performance!.Should()
    .HaveTokenCountUnder(2000);

// Prompt tokens
result.Performance!.Should()
    .HavePromptTokensUnder(500);

// Completion tokens
result.Performance!.Should()
    .HaveCompletionTokensUnder(1500);
```

### Cost Assertions

```csharp
// Estimated cost in USD
result.Performance!.Should()
    .HaveEstimatedCostUnder(0.10m, because: "batch processing must stay within budget");
```

### Tool Performance Assertions

```csharp
// Average tool execution time
result.Performance!.Should()
    .HaveAverageToolTimeUnder(TimeSpan.FromMilliseconds(200));

// Total tool execution time
result.Performance!.Should()
    .HaveTotalToolTimeUnder(TimeSpan.FromSeconds(2));

// Tool call count
result.Performance!.Should()
    .HaveToolCallCount(5);
```

## Response Assertions

### Content Assertions

```csharp
// Contains substring (case-insensitive by default)
result.ActualOutput!.Should()
    .Contain("success");

// Case-sensitive match
result.ActualOutput!.Should()
    .Contain("SUCCESS", caseSensitive: true);

// Contains all substrings
result.ActualOutput!.Should()
    .ContainAll("name", "email", "address");

// Contains any substring
result.ActualOutput!.Should()
    .ContainAny("approved", "accepted", "confirmed");

// Does NOT contain
result.ActualOutput!.Should()
    .NotContain("error")
    .NotContain("exception");
```

### Pattern Matching

```csharp
// Regex pattern matching
result.ActualOutput!.Should()
    .MatchPattern(@"\d{3}-\d{3}-\d{4}"); // Phone number

// Email pattern
result.ActualOutput!.Should()
    .MatchPattern(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
```

### Length Assertions

```csharp
// Length range
result.ActualOutput!.Should()
    .HaveLengthBetween(100, 500);

// Minimum length
result.ActualOutput!.Should()
    .HaveLengthAtLeast(50, because: "responses should be substantive");
```

### Structure Assertions

```csharp
// Not empty
result.ActualOutput!.Should()
    .NotBeEmpty();

// Starts with
result.ActualOutput!.Should()
    .StartWith("Hello");

// Ends with
result.ActualOutput!.Should()
    .EndWith("Thank you for your inquiry.");
```

## Exception Types

AgentEval provides structured exception types for programmatic handling:

| Exception Type | Properties |
|----------------|------------|
| `AgentEvalAssertionException` | `Expected`, `Actual`, `Context`, `Suggestions`, `Because` |
| `ToolAssertionException` | Above + `ToolName`, `CalledTools` |
| `PerformanceAssertionException` | Above + `MetricName`, `Threshold`, `MeasuredValue` |
| `ResponseAssertionException` | Above + `ResponsePreview` |
| `AgentEvalScopeException` | `Failures` (list of all collected failures) |

**Programmatic access example:**

```csharp
try
{
    result.ToolUsage!.Should().HaveCalledTool("MissingTool");
}
catch (ToolAssertionException ex)
{
    Console.WriteLine($"Expected: {ex.Expected}");
    Console.WriteLine($"Actual: {ex.Actual}");
    Console.WriteLine($"Tool: {ex.ToolName}");
    
    if (ex.Suggestions != null)
    {
        foreach (var suggestion in ex.Suggestions)
        {
            Console.WriteLine($"Suggestion: {suggestion}");
        }
    }
}
```

## Best Practices

### 1. Use "Because" for Documentation

```csharp
// ❌ Without context
result.ToolUsage!.Should().HaveCalledTool("AuthTool");

// ✅ With context
result.ToolUsage!.Should()
    .HaveCalledTool("AuthTool", because: "all API calls require authentication");
```

### 2. Use Scopes for Related Assertions

```csharp
// ❌ Stops at first failure
result.ToolUsage!.Should().HaveCalledTool("Tool1");
result.ToolUsage!.Should().HaveCalledTool("Tool2");  // Never runs if Tool1 fails

// ✅ Collects all failures
using (new AgentEvalScope("Verifying complete tool chain"))
{
    result.ToolUsage!.Should().HaveCalledTool("Tool1");
    result.ToolUsage!.Should().HaveCalledTool("Tool2");
    result.ToolUsage!.Should().HaveCalledTool("Tool3");
}
```

### 3. Chain Related Assertions

```csharp
// ✅ Fluent and readable
result.ToolUsage!.Should()
    .HaveCalledTool("SearchTool")
        .WithArgument("query", "test")
        .BeforeTool("ProcessTool")
        .WithoutError()
    .And()
    .HaveNoErrors();
```

### 4. Assert What Matters

```csharp
// ❌ Too brittle - exact count may vary
result.ToolUsage!.Should().HaveCallCount(3);

// ✅ More flexible - at least what's needed
result.ToolUsage!.Should().HaveCallCountAtLeast(1);
result.ToolUsage!.Should().HaveCalledTool("RequiredTool");
```

## See Also

- [Getting Started](getting-started.md) — Quick introduction to AgentEval
- [Architecture](architecture.md) — Understanding the component model
- [Extensibility](extensibility.md) — Creating custom metrics
- [Sample 02](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample02_AgentWithOneTool.cs) - Tool tracking and fluent assertions
- [Sample 03](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample03_AgentWithMultipleTools.cs) - Tool ordering assertions
- [Sample 12](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Sample12_PolicySafetyEvaluation.cs) - Policy and safety assertions
