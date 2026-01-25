# ADR-006: Service-Based Architecture with Dependency Injection

**Status:** Accepted & Implemented  
**Date:** 2026-01-10 (Proposed) | 2026-01-11 (Implemented)  
**Decision Makers:** AgentEval Contributors

---

## Context

The current AgentEval architecture has a mix of:
1. **Interfaces with implementations** (e.g., `IStochasticRunner`, `IModelComparer`, `IEvaluationHarness`)
2. **Static utility classes** (e.g., `StatisticsCalculator`, `ToolUsageExtractor`)
3. **Concrete class dependencies** (e.g., `ModelComparer` instantiates `new StochasticRunner()`)
4. **Wrapper interfaces over static utilities** (recent addition: `IStatisticsCalculator`, `IToolUsageExtractor`)

### Current Architecture Issues

1. **Mixed Patterns**: Some services are injectable (`IEvaluationHarness`), others are static utilities
2. **Tight Coupling**: `ModelComparer` directly instantiates `StochasticRunner` instead of using dependency injection
3. **Limited Testability**: Concrete dependencies make unit testing difficult without integration tests
4. **Inconsistent Design**: No clear pattern for when to use interfaces vs static classes
5. **Service Location Anti-Pattern**: Some code creates its own dependencies rather than receiving them

### Example of Current Tight Coupling

```csharp
public class ModelComparer : IModelComparer
{
    private readonly IEvaluationHarness _harness;
    
    public ModelComparer(IEvaluationHarness harness, EvaluationOptions? EvaluationOptions = null)
    {
        _harness = harness;
        _testOptions = EvaluationOptions;
    }
    
    public async Task<ModelComparisonResult> CompareModelsAsync(...)
    {
        // ❌ Directly instantiates dependency - tight coupling
        var stochasticRunner = new StochasticRunner(_harness, _testOptions);
        // ...
    }
}
```

### Current Interfaces Already Present

✅ **Good**: The codebase already has interfaces defined:
- `IStochasticRunner` ✓
- `IModelComparer` ✓
- `IEvaluationHarness` ✓
- `IEvaluableAgent` ✓
- `IAgentFactory` ✓
- `IStatisticsCalculator` ✓ (recently added)
- `IToolUsageExtractor` ✓ (recently added)
- `IMetric` ✓
- `IAgentEvalLogger` ✓

❌ **Problem**: Not all code uses these interfaces consistently

## Decision

**We will refactor the architecture to use consistent service-based design with dependency injection throughout.**

### Principles

1. **Program to Interfaces**: All dependencies should be interface-based, not concrete types
2. **Constructor Injection**: Dependencies injected via constructor (primary) or factory pattern
3. **Single Responsibility**: Each service has one clear responsibility
4. **Dependency Inversion**: High-level modules depend on abstractions
5. **Testability First**: All services mockable for unit testing

### Proposed Changes

#### Phase 1: Fix Concrete Dependencies (IMMEDIATE)

**Change 1**: Make `ModelComparer` receive `IStochasticRunner` instead of creating it

```csharp
// BEFORE (Current)
public class ModelComparer : IModelComparer
{
    private readonly IEvaluationHarness _harness;
    private readonly EvaluationOptions? _testOptions;
    
    public ModelComparer(IEvaluationHarness harness, EvaluationOptions? EvaluationOptions = null)
    {
        _harness = harness;
        _testOptions = EvaluationOptions;
    }
    
    public async Task<ModelComparisonResult> CompareModelsAsync(...)
    {
        var stochasticRunner = new StochasticRunner(_harness, _testOptions);
        // ...
    }
}

// AFTER (Proposed)
public class ModelComparer : IModelComparer
{
    private readonly IStochasticRunner _stochasticRunner;
    
    public ModelComparer(IStochasticRunner stochasticRunner)
    {
        _stochasticRunner = stochasticRunner ?? throw new ArgumentNullException(nameof(stochasticRunner));
    }
    
    public async Task<ModelComparisonResult> CompareModelsAsync(...)
    {
        // ✅ Uses injected dependency
        var result = await _stochasticRunner.RunStochasticTestAsync(...);
        // ...
    }
}
```

**Change 2**: Update `StochasticRunner` to accept dependencies via interface

```csharp
// CURRENT (Already good)
public class StochasticRunner : IStochasticRunner
{
    private readonly IEvaluationHarness _harness;  // ✅ Already using interface
    
    public StochasticRunner(IEvaluationHarness harness, EvaluationOptions? EvaluationOptions = null)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _testOptions = EvaluationOptions;
    }
}

// PROPOSED: Add IStatisticsCalculator dependency
public class StochasticRunner : IStochasticRunner
{
    private readonly IEvaluationHarness _harness;
    private readonly IStatisticsCalculator _statisticsCalculator;
    private readonly EvaluationOptions? _testOptions;
    
    public StochasticRunner(
        IEvaluationHarness harness, 
        IStatisticsCalculator? statisticsCalculator = null,
        EvaluationOptions? EvaluationOptions = null)
    {
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _statisticsCalculator = statisticsCalculator ?? DefaultStatisticsCalculator.Instance;
        _testOptions = EvaluationOptions;
    }
}
```

#### Phase 2: Service Registration (RECOMMENDED FOR FUTURE)

Create a service configuration extension for DI containers:

```csharp
// New file: AgentEvalServiceCollectionExtensions.cs
public static class AgentEvalServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEval(
        this IServiceCollection services, 
        Action<AgentEvalOptions>? configure = null)
    {
        var options = new AgentEvalOptions();
        configure?.Invoke(options);
        
        // Register core services
        services.AddSingleton<IStatisticsCalculator, DefaultStatisticsCalculator>();
        services.AddSingleton<IToolUsageExtractor, DefaultToolUsageExtractor>();
        
        // Register runners and comparers
        services.AddScoped<IStochasticRunner, StochasticRunner>();
        services.AddScoped<IModelComparer, ModelComparer>();
        
        // Register evaluation harness (user must provide or use default)
        if (options.TestHarness != null)
        {
            services.AddSingleton(options.TestHarness);
        }
        
        return services;
    }
}
```

#### Phase 3: Builder Pattern Enhancement (OPTIONAL)

Keep `AgentEvalBuilder` but make it use DI under the hood:

```csharp
public sealed class AgentEvalBuilder
{
    private readonly ServiceCollection _services = new();
    
    public AgentEvalBuilder WithTestHarness<T>() where T : class, IEvaluationHarness
    {
        _services.AddSingleton<IEvaluationHarness, T>();
        return this;
    }
    
    public AgentEvalBuilder WithStatisticsCalculator<T>() where T : class, IStatisticsCalculator
    {
        _services.AddSingleton<IStatisticsCalculator, T>();
        return this;
    }
    
    public IServiceProvider Build()
    {
        // Register defaults for anything not configured
        _services.TryAddSingleton<IStatisticsCalculator, DefaultStatisticsCalculator>();
        _services.TryAddSingleton<IToolUsageExtractor, DefaultToolUsageExtractor>();
        
        return _services.BuildServiceProvider();
    }
}
```

## Consequences

### Positive

1. **Improved Testability**: All dependencies can be mocked easily
2. **Better Separation of Concerns**: Each service has clear, testable boundaries
3. **Flexibility**: Easy to swap implementations (e.g., custom statistics calculator)
4. **Consistency**: Uniform pattern across the codebase
5. **Maintainability**: Changes to one service don't cascade to others
6. **Industry Standard**: Follows .NET DI best practices

### Negative

1. **Migration Effort**: Existing code needs updates (but can be gradual)
2. **Slight Complexity**: Users need to understand DI concepts
3. **Breaking Changes**: Some constructors will change signatures

### Neutral

1. **Backward Compatibility**: Can maintain static methods as facade over interfaces
2. **Learning Curve**: Developers familiar with DI patterns won't be affected

## Implementation Plan

### Stage 1: Fix Immediate Coupling Issues (Low Risk)

**Files to Change:**
1. `src/AgentEval/Comparison/ModelComparer.cs`
   - Add `IStochasticRunner` constructor parameter
   - Remove `new StochasticRunner()` instantiation
   
2. `src/AgentEval/Comparison/StochasticRunner.cs`
   - Add `IStatisticsCalculator` optional constructor parameter
   - Use injected calculator instead of static calls

**Benefits:**
- Eliminates tight coupling
- Improves testability
- No breaking changes for users (builder/factory can create instances)

**Testing:**
- All existing tests should pass
- Can add unit tests with mocked dependencies

### Stage 2: Add Service Registration (Medium Risk)

**New Files:**
1. `src/AgentEval/DependencyInjection/AgentEvalServiceCollectionExtensions.cs`
2. `src/AgentEval/DependencyInjection/AgentEvalOptions.cs`

**Benefits:**
- Opt-in for users who want DI
- Follows .NET conventions
- Easy integration with ASP.NET Core, Worker Services, etc.

**Testing:**
- Integration tests with DI container
- Verify service resolution

### Stage 3: Document Migration Path (Low Risk)

**New Documentation:**
1. Update README with DI examples
2. Add migration guide for existing code
3. Document when to use static vs DI approaches

## Decision Rationale

### Why Now?

1. **Interfaces Already Exist**: The groundwork (interfaces) is already in place
2. **Recent Additions**: Just added `IStatisticsCalculator` and `IToolUsageExtractor`
3. **Natural Evolution**: Code is ready for this architectural improvement
4. **User Request**: Explicit request to review IOC/DI patterns

### Why Gradual Approach?

1. **Risk Mitigation**: Incremental changes reduce risk of breakage
2. **Backward Compatibility**: Existing code continues to work
3. **User Choice**: Developers can adopt at their own pace
4. **Testing**: Each stage can be thoroughly tested before proceeding

### Why Not Full DI Everywhere?

1. **Simple Utilities**: Some static utilities (like `JsonParsingHelper`) don't need DI
2. **User Experience**: Not all users want/need DI complexity
3. **Pragmatism**: Balance between "pure" architecture and practical usability

## Alternatives Considered

### Alternative 1: Keep Status Quo

**Pros:**
- No migration effort
- No breaking changes

**Cons:**
- Technical debt accumulates
- Testing remains difficult
- Inconsistent patterns persist

**Verdict:** ❌ Rejected - doesn't address core issues

### Alternative 2: Full Rewrite with Pure DI

**Pros:**
- Clean, consistent architecture
- Best practices throughout

**Cons:**
- Major breaking changes
- High migration cost for users
- Overkill for simple use cases

**Verdict:** ❌ Rejected - too disruptive

### Alternative 3: Gradual Refactoring (CHOSEN)

**Pros:**
- Incremental improvements
- Backward compatible
- User choice

**Cons:**
- Takes longer
- Temporary inconsistency during migration

**Verdict:** ✅ **ACCEPTED** - best balance

## References

- [Dependency Injection in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection)
- [SOLID Principles](https://en.wikipedia.org/wiki/SOLID)
- [Dependency Inversion Principle](https://en.wikipedia.org/wiki/Dependency_inversion_principle)
- Martin Fowler on [Inversion of Control](https://martinfowler.com/bliki/InversionOfControl.html)

## Review Comments

**Review requested by:** @joslat  
**Date:** 2026-01-10

> @copilot review the code and pr please.
> Also do recheck the interfaces again for IOC and DI and proper testing. Also, would it make sense to code for interfaces instead of implementations?
> How about having separate services created and injected for decoupling? If you think that makes sense, please analyse and propose an implementation plan as an ADR, as this changes the architecture, right?

**Response:** This ADR addresses the architectural concerns raised. The gradual approach allows us to improve the architecture without breaking existing code, starting with fixing the immediate coupling issues in `ModelComparer`.

---

## Implementation Status

**Phase 1**: ✅ **COMPLETE** (Commit b191a1b)
- Fixed tight coupling in `ModelComparer` to receive `IStochasticRunner` via DI
- Enhanced `StochasticRunner` to accept `IStatisticsCalculator` via DI
- Added `[Obsolete]` and `[ActivatorUtilitiesConstructor]` attributes

**Phase 2**: ✅ **COMPLETE** (Commits fa9aa58, dc8b0ed, 7b05d75)
- Created `AgentEvalServiceCollectionExtensions` with full service registration
- Added `AgentEvalServiceOptions` for configurable lifetimes
- Implemented `AddAgentEval()`, `AddAgentEvalScoped()`, `AddAgentEvalSingleton()`, `AddAgentEvalTransient()`
- Created 9 comprehensive DI tests
- Enhanced all interfaces with complete API coverage and documentation
- Verified 100% interface coverage across all service areas

**Phase 3**: 🔄 **OPTIONAL** (Future enhancement)
- Builder pattern enhancements can be added as needed
- Current architecture is production-ready

**Final Status**: **ACCEPTED & IMPLEMENTED** - All core objectives achieved with 100% backward compatibility.
