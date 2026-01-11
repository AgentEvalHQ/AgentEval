---
applyTo: "src/AgentEval/Assertions/**/*.cs"
description: Guidelines for implementing fluent assertions
---

# Assertion Implementation Guidelines

## Core Assertion Pattern
All assertions follow the fluent pattern with chainable methods:
```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("Tool1")
        .BeforeTool("Tool2")
        .WithArgument("key", "value")
    .And()  // Return to parent assertions
    .HaveNoErrors();
```

## Adding New Assertion Method
1. Add to appropriate assertions class: `ToolUsageAssertions`, `PerformanceAssertions`, or `ResponseAssertions`
2. Always add `[StackTraceHidden]` attribute to keep stack traces clean
3. Accept optional `because` parameter for documentation
4. Use `AgentEvalScope.FailWith()` for rich error messages

## Assertion Method Template
```csharp
/// <summary>Assert that XYZ condition is met.</summary>
/// <param name="expected">The expected value.</param>
/// <param name="because">Optional reason for the assertion.</param>
[StackTraceHidden]
public ToolUsageAssertions HaveXyz(string expected, string? because = null)
{
    if (!ConditionMet(expected))
    {
        AgentEvalScope.FailWith(
            ToolAssertionException.Create(
                $"Expected XYZ to be '{expected}', but it was not.",
                expected: expected,
                actual: _actualValue,
                suggestions: new[] { "Try doing X", "Check Y" },
                because: because));
    }
    return this;
}
```

## Error Message Structure
All failure messages MUST include:
- **Expected**: What was expected
- **Actual**: What was found
- **Suggestions**: Actionable hints (when applicable)
- **Because**: User's reason (if provided)

Example output:
```
Expected tool 'SearchTool' to be called because query requires search.

Expected: Tool 'SearchTool' called at least once
Actual:   Tools called: [CalculateTool, FormatTool]

Suggestions:
  → Verify the agent has access to the expected tools
  → Check if the prompt clearly requests tool usage
```

## Exception Types
- `ToolAssertionException` - Tool-related assertion failures
- `PerformanceAssertionException` - Performance/cost assertion failures
- `ResponseAssertionException` - Response content assertion failures
- `AgentEvalScopeException` - Multiple failures collected in scope

## Using AgentEvalScope
For collecting multiple failures before throwing:
```csharp
using (new AgentEvalScope())
{
    result.ToolUsage!.Should().HaveCalledTool("A");
    result.ToolUsage!.Should().HaveCalledTool("B");
    result.Performance!.Should().HaveDurationUnder(TimeSpan.FromSeconds(5));
}
// Throws AgentEvalScopeException with ALL failures listed
```

## Return Types for Chaining
- Return `this` for continuation (`And()` pattern)
- Return child assertion class for drill-down (e.g., `ToolCallAssertion`)
- Provide `And()` method on child classes to return to parent
