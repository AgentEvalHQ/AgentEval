---
applyTo: "tests/**/*.cs"
description: Guidelines for writing AgentEval unit tests
---

# AgentEval Test Guidelines

## Test Naming Convention
Use: `MethodName_StateUnderTest_ExpectedBehavior`
```csharp
[Fact]
public async Task HaveCalledTool_WhenToolWasCalled_ShouldPass()
```

## Test Structure
- Tests mirror `src/` folder structure in `tests/AgentEval.Tests/`
- Use xUnit with `[Fact]` and `[Theory]` attributes
- Each public class/method should have corresponding tests

## Using FakeChatClient
For metrics that call LLMs, use `FakeChatClient` to avoid API calls:
```csharp
var fakeClient = new FakeChatClient("""{"score": 95, "explanation": "Good"}""");
var metric = new FaithfulnessMetric(fakeClient);
var result = await metric.EvaluateAsync(context);
```

## Creating ToolUsageReport for Tests
```csharp
var report = new ToolUsageReport(new List<ToolCallRecord>
{
    new() { Name = "SearchTool", CallId = "call-1", Result = "found" },
    new() { Name = "ProcessTool", CallId = "call-2", Result = "done" }
});
```

## Creating PerformanceMetrics for Tests
```csharp
var metrics = new PerformanceMetrics
{
    TotalDuration = TimeSpan.FromSeconds(2.5),
    TimeToFirstToken = TimeSpan.FromMilliseconds(250),
    PromptTokens = 100,
    CompletionTokens = 50,
    ModelUsed = "gpt-4o"
};
```

## Assertion Tests Pattern
When testing assertions, expect `ToolAssertionException` or `PerformanceAssertionException`:
```csharp
[Fact]
public void HaveCalledTool_WhenToolNotCalled_ShouldThrow()
{
    var report = new ToolUsageReport([]);
    
    var ex = Assert.Throws<ToolAssertionException>(() =>
        report.Should().HaveCalledTool("MissingTool"));
    
    Assert.Contains("MissingTool", ex.Message);
    Assert.NotNull(ex.Expected);
    Assert.NotNull(ex.Actual);
}
```

## Multi-Target Framework Testing
Tests run on net8.0, net9.0, net10.0 - ensure compatibility. Use `#if NET8_0` sparingly.
