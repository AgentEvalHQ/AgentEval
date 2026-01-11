```instructions
---
applyTo: "src/AgentEval/DependencyInjection/**/*.cs"
description: Guidelines for dependency injection and service registration
---

# Dependency Injection Guidelines

## Core Principle: Interface-First Development

All core services must depend on abstractions, not concretions. See ADR-006 for full details.

## Service Registration Pattern

```csharp
public static class AgentEvalServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEval(
        this IServiceCollection services,
        Action<AgentEvalServiceOptions>? configure = null)
    {
        var options = new AgentEvalServiceOptions();
        configure?.Invoke(options);
        
        // Register singleton services (stateless)
        services.AddSingleton<IStatisticsCalculator, DefaultStatisticsCalculator>();
        services.AddSingleton<IToolUsageExtractor, DefaultToolUsageExtractor>();
        
        // Register scoped services (stateful per-operation)
        services.AddScoped<IStochasticRunner, StochasticRunner>();
        services.AddScoped<IModelComparer>(sp => 
            new ModelComparer(sp.GetRequiredService<IStochasticRunner>()));
        
        return services;
    }
}
```

## Service Lifetime Guidelines

### Singleton (stateless)
Use for services that:
- Have no mutable state
- Are thread-safe
- Can be shared across all requests

**Examples**: `IStatisticsCalculator`, `IToolUsageExtractor`

### Scoped (stateful per-operation)
Use for services that:
- Maintain state during a single operation
- Need fresh instance per test run
- Have dependencies on other scoped services

**Examples**: `IStochasticRunner`, `IModelComparer`

### Transient (new each time)
Use sparingly for:
- Lightweight, disposable objects
- Objects with very short lifecycles

## When NOT to Register as Services

Per service-gap-analysis.md, these should NOT be in DI:
- **Builders**: `AgentEvalBuilder` (fluent API - direct instantiation)
- **Configuration POCOs**: `StochasticOptions`, `ModelComparisonOptions`
- **Test-time tools**: `PerformanceBenchmark`, `SnapshotComparer`

## Adding a New Service

1. **Define interface** in `Core/` or domain folder:
   ```csharp
   public interface IMyService
   {
       Task<Result> DoWorkAsync(Input input);
   }
   ```

2. **Implement interface**:
   ```csharp
   public class MyService : IMyService
   {
       private readonly IDependency _dependency;
       
       public MyService(IDependency dependency)
       {
           _dependency = dependency;
       }
       
       public async Task<Result> DoWorkAsync(Input input) { ... }
   }
   ```

3. **Register in DI**:
   ```csharp
   services.AddScoped<IMyService, MyService>();
   ```

4. **Inject via constructor** (never resolve manually):
   ```csharp
   public class Consumer(IMyService myService) { }
   ```

## SOLID Principles in DI

### Dependency Inversion (D in SOLID)
```csharp
// ❌ BAD: Depending on concretion
public class ModelComparer
{
    private readonly StochasticRunner _runner; // concrete!
}

// ✅ GOOD: Depending on abstraction
public class ModelComparer : IModelComparer
{
    private readonly IStochasticRunner _runner; // interface!
}
```

### Interface Segregation (I in SOLID)
```csharp
// ✅ GOOD: Separate interfaces for distinct capabilities
public interface ITestableAgent { Task<string> ExecuteAsync(string prompt); }
public interface IStreamableAgent : ITestableAgent { IAsyncEnumerable<string> StreamAsync(string prompt); }
```

## Testing with DI

For unit tests, inject mocks directly:
```csharp
var mockRunner = new Mock<IStochasticRunner>();
var comparer = new ModelComparer(mockRunner.Object);
```

For integration tests, use the full DI container:
```csharp
var services = new ServiceCollection();
services.AddAgentEval();
var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IStochasticRunner>();
```
```
