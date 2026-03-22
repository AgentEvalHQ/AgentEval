using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Metrics;
using AgentEval.Memory.Reporting;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Memory.Extensions;

/// <summary>
/// Extension methods for registering AgentEval.Memory services with dependency injection.
/// Follows ADR-006 service-based architecture patterns.
/// </summary>
public static class AgentEvalMemoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers all AgentEval.Memory services with the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to add memory services to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemory(this IServiceCollection services)
    {
        // Core memory services (Scoped - stateful per evaluation)
        services.AddScoped<IMemoryTestRunner, MemoryTestRunner>();
        services.AddScoped<IMemoryJudge, MemoryJudge>();
        services.AddScoped<ITemporalMemoryRunner, TemporalMemoryRunner>();
        
        // Evaluators (Scoped - stateful per evaluation, depend on scoped runner/judge)
        services.AddScoped<IReachBackEvaluator, ReachBackEvaluator>();
        services.AddScoped<IReducerEvaluator, ReducerEvaluator>();
        services.AddScoped<ICrossSessionEvaluator, CrossSessionEvaluator>();
        services.AddScoped<IMemoryBenchmarkRunner, MemoryBenchmarkRunner>();
        
        // Scenario providers (Singleton - stateless factory services)
        services.AddSingleton<IMemoryScenarios, MemoryScenarios>();
        services.AddSingleton<IChattyConversationScenarios, ChattyConversationScenarios>();
        services.AddSingleton<ICrossSessionScenarios, CrossSessionScenarios>();
        services.AddSingleton<ITemporalMemoryScenarios, TemporalMemoryScenarios>();
        
        // Memory-specific metrics (Transient - lightweight per evaluation)
        services.AddTransient<MemoryRetentionMetric>();
        services.AddTransient<MemoryTemporalMetric>();
        services.AddTransient<MemoryReachBackMetric>();
        services.AddTransient<MemoryNoiseResilienceMetric>();
        services.AddTransient<MemoryReducerFidelityMetric>();

        // Reporting services
        services.AddAgentEvalMemoryReporting();

        return services;
    }
    
    /// <summary>
    /// Registers only core memory evaluation services (runner and judge).
    /// Use when you need basic memory testing without scenarios and metrics.
    /// </summary>
    /// <param name="services">The service collection to add core services to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryCore(this IServiceCollection services)
    {
        services.AddScoped<IMemoryTestRunner, MemoryTestRunner>();
        services.AddScoped<IMemoryJudge, MemoryJudge>();
        
        return services;
    }
    
    /// <summary>
    /// Registers memory scenario providers only.
    /// Use when you want to create custom scenarios or use scenarios independently.
    /// </summary>
    /// <param name="services">The service collection to add scenario services to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryScenarios(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryScenarios, MemoryScenarios>();
        services.AddSingleton<IChattyConversationScenarios, ChattyConversationScenarios>();
        services.AddSingleton<ICrossSessionScenarios, CrossSessionScenarios>();
        services.AddSingleton<ITemporalMemoryScenarios, TemporalMemoryScenarios>();
        
        return services;
    }
    
    /// <summary>
    /// Registers memory-specific metrics only.
    /// Use when you want to use memory metrics in custom evaluation pipelines.
    /// </summary>
    /// <param name="services">The service collection to add memory metrics to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryMetrics(this IServiceCollection services)
    {
        services.AddTransient<MemoryRetentionMetric>();
        services.AddTransient<MemoryTemporalMetric>();
        services.AddTransient<MemoryReachBackMetric>();
        services.AddTransient<MemoryNoiseResilienceMetric>();
        services.AddTransient<MemoryReducerFidelityMetric>();
        
        return services;
    }
    
    /// <summary>
    /// Registers temporal memory evaluation services.
    /// Use when you need time-travel queries and temporal reasoning capabilities.
    /// </summary>
    /// <param name="services">The service collection to add temporal services to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryTemporal(this IServiceCollection services)
    {
        services.AddScoped<ITemporalMemoryRunner, TemporalMemoryRunner>();
        services.AddSingleton<ITemporalMemoryScenarios, TemporalMemoryScenarios>();
        services.AddTransient<MemoryTemporalMetric>();
        
        return services;
    }

    /// <summary>
    /// Registers advanced memory evaluators (reach-back, reducer, cross-session, benchmark).
    /// Use when you need the full evaluation pipeline beyond basic memory testing.
    /// Requires core services to be registered first.
    /// </summary>
    /// <param name="services">The service collection to add evaluator services to.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryEvaluators(this IServiceCollection services)
    {
        services.AddScoped<IReachBackEvaluator, ReachBackEvaluator>();
        services.AddScoped<IReducerEvaluator, ReducerEvaluator>();
        services.AddScoped<ICrossSessionEvaluator, CrossSessionEvaluator>();
        services.AddScoped<IMemoryBenchmarkRunner, MemoryBenchmarkRunner>();

        return services;
    }

    /// <summary>
    /// Registers memory benchmark reporting services (baseline persistence, comparison, options).
    /// </summary>
    /// <param name="services">The service collection to add reporting services to.</param>
    /// <param name="configure">Optional configuration action for <see cref="MemoryReportingOptions"/>.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddAgentEvalMemoryReporting(
        this IServiceCollection services,
        Action<MemoryReportingOptions>? configure = null)
    {
        var options = new MemoryReportingOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddScoped<IBaselineStore, JsonFileBaselineStore>();
        services.AddScoped<IBaselineComparer, BaselineComparer>();

        return services;
    }
}