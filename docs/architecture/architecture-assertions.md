# AgentEval Assertions Architecture

> **Status**: Analysis Complete  
> **Last Updated**: January 13, 2026  
> **Purpose**: Deep analysis of the AgentEval assertions system, xUnit integration, and comparison with industry-standard assertion libraries.

## Table of Contents

1. [Current Architecture Overview](#current-architecture-overview)
2. [xUnit Integration Analysis](#xunit-integration-analysis)
3. [Comparison with Industry Standards](#comparison-with-industry-standards)
4. [Key Question: Framework-Specific Exceptions](#key-question-framework-specific-exceptions)
5. [Gap Analysis](#gap-analysis)
6. [Service Architecture Integration](#service-architecture-integration)
7. [Recommendations](#recommendations)

---

## Current Architecture Overview

### Class Hierarchy

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       ASSERTION EXCEPTION HIERARCHY                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  Exception (System)                                                         │
│  └── AgentEvalAssertionException                                            │
│      ├── ToolAssertionException                                             │
│      │   └── BehavioralPolicyViolationException                             │
│      ├── PerformanceAssertionException                                      │
│      ├── ResponseAssertionException                                         │
│      ├── WorkflowAssertionException                                         │
│      └── AgentEvalScopeException (collects multiple failures)               │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Fluent Assertions Entry Points

| Entry Point | Class | Usage |
|------------|-------|-------|
| `result.ToolUsage!.Should()` | `ToolUsageAssertions` | Tool call verification |
| `result.Performance!.Should()` | `PerformanceAssertions` | Latency, tokens, cost |
| `result.ActualOutput!.Should()` | `ResponseAssertions` | Response content |
| `workflowResult.Should()` | `WorkflowAssertions` | Workflow execution |
| `stochasticResult.Should()` | `StochasticAssertions` | Statistical testing |

### Exception Properties

All AgentEval assertion exceptions provide structured context:

```csharp
public class AgentEvalAssertionException : Exception
{
    public string? Expected { get; init; }     // Expected value/condition
    public string? Actual { get; init; }       // Actual value found
    public string? Context { get; init; }      // Additional context (timeline, etc.)
    public IReadOnlyList<string>? Suggestions { get; init; } // Actionable fix suggestions
    public string? SubjectName { get; init; }  // Variable name being tested
    public string? Because { get; init; }      // User-provided reason
}
```

### AgentEvalScope (Assertion Batching)

Similar to FluentAssertions' `AssertionScope`, AgentEval provides scope-based assertion collection:

```csharp
using (new AgentEvalScope("Verifying agent behavior"))
{
    result.ToolUsage!.Should().HaveCalledTool("SearchTool");
    result.ToolUsage!.Should().HaveCalledTool("ProcessTool");
    result.Performance!.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5));
}
// Throws AgentEvalScopeException with ALL failures if any occurred
```

**Implementation Pattern:**
- Uses `[ThreadStatic]` for scope tracking (thread-safe per-test)
- `RecordFailure()` collects exceptions when scope is active
- `FailWith()` either throws immediately or records for later
- Dispose throws `AgentEvalScopeException` if failures were collected

---

## xUnit Integration Analysis

### Key Question Answered

> **"Does AgentEval throw framework-specific exceptions when an assertion fails?"**

**Answer: NO. AgentEval currently throws its own `AgentEvalAssertionException` hierarchy, not xUnit-specific exceptions.**

### Current Behavior

When an AgentEval assertion fails:

1. A domain-specific exception is created (e.g., `ToolAssertionException`)
2. `AgentEvalScope.FailWith()` is called
3. If no scope is active, the exception is thrown immediately
4. If a scope is active, the exception is collected

### xUnit Compatibility

**Good News**: xUnit (and most test frameworks) correctly identifies and reports ANY exception as a test failure, not just `Xunit.Sdk.XunitException`.

```csharp
[Fact]
public void AgentEvalExceptions_AreCorrectlyReported()
{
    // This DOES cause xUnit to report a failure
    var exception = Assert.Throws<ToolAssertionException>(() => 
        report.Should().HaveCalledTool("MissingTool"));
    
    // xUnit captures the exception and reports the test as failed
}
```

**What This Means:**
- ✅ Tests correctly fail when AgentEval assertions fail
- ✅ Exception messages are displayed in test output
- ✅ Stack traces work correctly (with `[StackTraceHidden]`)
- ⚠️ Some xUnit-specific features may not work optimally

### What We're Missing (vs FluentAssertions)

| Feature | FluentAssertions | AgentEval Current |
|---------|------------------|-------------------|
| **Exception Type** | Throws `XunitException` | Throws `AgentEvalAssertionException` |
| **Framework Detection** | Auto-detects xUnit/NUnit/MSTest | Single exception type |
| **Visual Studio Integration** | Optimal "Expected vs Actual" diff | Generic exception display |
| **CI/CD Parsing** | Framework-native exception parsing | Generic exception |
| **Subject Identification** | Extracts variable name from source | Not implemented |
| **Source Code Location** | Reads source line from PDB | Uses `[StackTraceHidden]` only |

---

## Comparison with Industry Standards

### FluentAssertions Architecture

**Framework Detection:**
```
FluentAssertions detects frameworks at runtime:
1. Scans loaded assemblies for known framework types
2. Selects appropriate exception thrower
3. Throws framework-native exception (e.g., Xunit.Sdk.XunitException)
```

**Key Classes:**
- `TestFrameworkProvider` - Discovers available frameworks
- `ITestFramework` - Interface for framework adapters
- Framework-specific implementations (XUnit2TestFramework, NUnitTestFramework, etc.)

**Configuration:**
```csharp
// FluentAssertions allows explicit framework selection
GlobalConfiguration.TestFramework = TestFramework.XUnit2;
```

### Shouldly Architecture

**Stack Trace Parsing:**
```
Shouldly reads source code to provide context:
1. Parses stack trace to find assertion location
2. Reads .cs file from disk using PDB info
3. Extracts the actual assertion statement
4. Includes in failure message
```

**Example Output:**
```
contestant.Points
    should be
1337
    but was
0
```

**Requirements:**
- PDB files must be present (even on build servers)
- Source files accessible at compile-time paths

### Comparison Table

| Aspect | FluentAssertions | Shouldly | AgentEval |
|--------|------------------|----------|-----------|
| **Syntax** | `.Should().Be()` | `.ShouldBe()` | `.Should().HaveCalledTool()` |
| **Exception Strategy** | Framework-specific | Custom + Source | Domain-specific hierarchy |
| **Subject Identification** | Reflection + Source | Source parsing | Not implemented |
| **"Because" Support** | ✅ Full | ⚠️ Partial | ✅ Full |
| **Assertion Scope** | ✅ `AssertionScope` | ❌ No | ✅ `AgentEvalScope` |
| **Domain-Specific** | ❌ Generic | ❌ Generic | ✅ AI Agent focused |
| **Structured Exceptions** | ⚠️ Message only | ⚠️ Message only | ✅ Properties + Suggestions |
| **Licensing** | Paid (v8+) | MIT | MIT |

---

## Gap Analysis

### What AgentEval Does Well ✅

1. **Domain-Specific Assertions**: Purpose-built for AI agent testing
   - Tool usage verification
   - Performance/cost assertions  
   - Behavioral policy assertions (NeverCallTool, NeverPassArgumentMatching)
   - Workflow assertions

2. **Structured Exception Data**: Rich programmatic access
   ```csharp
   catch (ToolAssertionException ex)
   {
       var tool = ex.ToolName;           // Structured data
       var called = ex.CalledTools;      // Not just strings
       var suggestions = ex.Suggestions; // Actionable help
   }
   ```

3. **"Because" Support**: Full implementation across all assertion types

4. **Assertion Scopes**: Proper batching with `AgentEvalScope`

5. **Fluent Chaining**: Natural reading order
   ```csharp
   result.ToolUsage!.Should()
       .HaveCalledTool("Auth")
           .BeforeTool("API")
           .WithArgument("type", "OAuth2")
       .And()
       .HaveNoErrors();
   ```

### What's Missing (Gaps) ⚠️

| Gap | Priority | Complexity | Impact |
|-----|----------|------------|--------|
| **Framework-Specific Exceptions** | Medium | High | Better IDE integration |
| **Subject Identification** | Low | High | Better failure messages |
| **Source Location** | Low | Medium | Debugging aid |
| **Parallel Assertion Collection** | Low | Medium | Advanced scenarios |

### Analysis: Do We Need Framework-Specific Exceptions?

**Arguments FOR:**
1. Optimal Visual Studio/Rider integration with "Expected vs Actual" diff view
2. CI/CD tools may have special handling for framework exceptions
3. Consistency with FluentAssertions (familiar to many developers)

**Arguments AGAINST:**
1. xUnit already reports failures correctly with any exception type
2. Our domain-specific exceptions carry more semantic information
3. FluentAssertions v8+ is now paid for commercial use
4. Adding framework detection adds complexity and maintenance burden
5. We're not a general-purpose assertion library

**Recommendation**: LOW PRIORITY - Current approach works well. Consider only if user feedback indicates specific integration issues.

---

## Service Architecture Integration

### Current Implementation

The assertions system is currently **not integrated with DI/IOC**. This is intentional per ADR-006:

> "Don't add interfaces for builders, configuration objects, or test-time tools"

Assertions are test-time tools used in fluent patterns, making DI registration unnecessary.

### Extension Point Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        ASSERTION EXTENSION POINTS                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  Extension Method Entry Points:                                             │
│  ├── ToolUsageReport.Should() → ToolUsageAssertions                         │
│  ├── PerformanceMetrics.Should() → PerformanceAssertions                    │
│  ├── string.Should() → ResponseAssertions                                   │
│  └── WorkflowExecutionResult.Should() → WorkflowAssertions                  │
│                                                                             │
│  Scope Integration:                                                         │
│  └── AgentEvalScope.FailWith() ← All assertions flow through here           │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Adding New Assertions (Pattern)

To add new assertion types:

1. Create assertions class (e.g., `ConversationAssertions`)
2. Add extension method `MyType.Should()`
3. Use `[StackTraceHidden]` on assertion methods
4. Call `AgentEvalScope.FailWith()` for failures
5. Create domain-specific exception if needed

```csharp
public class ConversationAssertions
{
    private readonly ChatExecutionResult _result;
    
    [StackTraceHidden]
    public ConversationAssertions HaveTurnCount(int expected, string? because = null)
    {
        if (_result.Turns.Count != expected)
        {
            AgentEvalScope.FailWith(
                AgentEvalAssertionException.Create(
                    $"Expected {expected} turns, but found {_result.Turns.Count}",
                    expected: expected.ToString(),
                    actual: _result.Turns.Count.ToString(),
                    because: because));
        }
        return this;
    }
}
```

---

## Recommendations

### Short-Term (Keep Current Approach)

The current assertion architecture is **well-designed and functional**:

1. ✅ Domain-specific exceptions with structured data
2. ✅ Fluent API with full "because" support
3. ✅ Assertion scopes for batching
4. ✅ Works correctly with xUnit (and other frameworks)
5. ✅ Clean stack traces via `[StackTraceHidden]`

**No immediate changes required.**

### Medium-Term Enhancements (Optional)

1. **Test Framework Detection (Low Priority)**
   - Add optional framework detection
   - Wrap our exceptions in framework-native exceptions
   - Only if user feedback indicates need

2. **Custom Assertion Documentation**
   - Add guide for creating custom assertions
   - Document extension patterns

3. **Formatter Customization**
   - Allow customizing how values are formatted in messages
   - Useful for complex AI response formatting

### Long-Term Considerations

1. **Subject Identification**
   - Could use Roslyn source generators for compile-time extraction
   - High complexity, low priority

2. **Diff Visualization**
   - For long string comparisons (AI responses)
   - Side-by-side diff in failure messages

---

## Summary

AgentEval's assertion system is well-architected for its purpose as an AI agent testing framework. While it doesn't throw framework-specific exceptions like FluentAssertions, this is by design:

1. **xUnit compatibility is NOT broken** - tests fail correctly
2. **Domain-specific exceptions provide MORE value** than generic framework exceptions
3. **Structured exception properties** enable programmatic handling
4. **The "because" pattern and assertion scopes** match industry standards

The key differentiator is that AgentEval assertions are **purpose-built for AI agent testing**, not general-purpose assertions. This specialization is a feature, not a limitation.

---

## References

- [docs/assertions.md](../assertions.md) - User-facing documentation
- [ADR-006](../adr/006-service-based-architecture-di.md) - Service architecture decisions
- [FluentAssertions Documentation](https://fluentassertions.com/introduction)
- [Shouldly Documentation](https://docs.shouldly.org/)
