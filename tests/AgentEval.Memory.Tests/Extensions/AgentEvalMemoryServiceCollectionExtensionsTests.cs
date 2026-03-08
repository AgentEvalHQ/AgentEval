using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Extensions;

public class AgentEvalMemoryServiceCollectionExtensionsTests
{
    private static ServiceCollection CreateServicesWithDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IChatClient>(new FakeChatClientForDI());
        return services;
    }

    [Fact]
    public void AddAgentEvalMemory_RegistersAllServices()
    {
        var services = CreateServicesWithDependencies();

        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();

        // Core services (scoped)
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryTestRunner>());
        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryJudge>());
        Assert.NotNull(scope.ServiceProvider.GetService<ITemporalMemoryRunner>());

        // Scenario providers (singleton)
        Assert.NotNull(provider.GetService<IMemoryScenarios>());
        Assert.NotNull(provider.GetService<IChattyConversationScenarios>());
        Assert.NotNull(provider.GetService<ICrossSessionScenarios>());
        Assert.NotNull(provider.GetService<ITemporalMemoryScenarios>());
    }

    [Fact]
    public void AddAgentEvalMemoryCore_RegistersOnlyCoreServices()
    {
        var services = CreateServicesWithDependencies();

        services.AddAgentEvalMemoryCore();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryTestRunner>());
        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryJudge>());
        Assert.Null(provider.GetService<IMemoryScenarios>());
    }

    [Fact]
    public void AddAgentEvalMemoryScenarios_RegistersOnlyScenarios()
    {
        var services = new ServiceCollection();

        services.AddAgentEvalMemoryScenarios();

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IMemoryScenarios>());
        Assert.NotNull(provider.GetService<IChattyConversationScenarios>());
        Assert.NotNull(provider.GetService<ICrossSessionScenarios>());
        Assert.NotNull(provider.GetService<ITemporalMemoryScenarios>());
    }

    [Fact]
    public void AddAgentEvalMemoryTemporal_RegistersTemporalServices()
    {
        var services = CreateServicesWithDependencies();

        // Temporal services depend on core services
        services.AddAgentEvalMemoryCore();
        services.AddAgentEvalMemoryTemporal();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<ITemporalMemoryRunner>());
        Assert.NotNull(provider.GetService<ITemporalMemoryScenarios>());
    }

    [Fact]
    public void AddAgentEvalMemory_CanResolveMemoryTestRunner()
    {
        var services = CreateServicesWithDependencies();
        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMemoryTestRunner>();

        Assert.IsType<MemoryTestRunner>(runner);
    }

    [Fact]
    public void AddAgentEvalMemory_CanResolveTemporalMemoryRunner()
    {
        var services = CreateServicesWithDependencies();
        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITemporalMemoryRunner>();

        Assert.IsType<TemporalMemoryRunner>(runner);
    }

    [Fact]
    public void AddAgentEvalMemory_RegistersEvaluators()
    {
        var services = CreateServicesWithDependencies();
        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IReachBackEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<IReducerEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<ICrossSessionEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryBenchmarkRunner>());
    }

    [Fact]
    public void AddAgentEvalMemoryEvaluators_RegistersOnlyEvaluators()
    {
        var services = CreateServicesWithDependencies();
        // Evaluators depend on core + scenarios
        services.AddAgentEvalMemoryCore();
        services.AddAgentEvalMemoryScenarios();
        services.AddAgentEvalMemoryTemporal();
        services.AddAgentEvalMemoryEvaluators();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IReachBackEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<IReducerEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<ICrossSessionEvaluator>());
        Assert.NotNull(scope.ServiceProvider.GetService<IMemoryBenchmarkRunner>());
    }

    [Fact]
    public void AddAgentEvalMemory_CanResolveReachBackEvaluator()
    {
        var services = CreateServicesWithDependencies();
        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IReachBackEvaluator>();

        Assert.IsType<ReachBackEvaluator>(evaluator);
    }

    [Fact]
    public void AddAgentEvalMemory_CanResolveMemoryBenchmarkRunner()
    {
        var services = CreateServicesWithDependencies();
        services.AddAgentEvalMemory();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMemoryBenchmarkRunner>();

        Assert.IsType<MemoryBenchmarkRunner>(runner);
    }

    /// <summary>
    /// Minimal IChatClient stub for DI resolution testing.
    /// </summary>
    private sealed class FakeChatClientForDI : IChatClient
    {
        public void Dispose() { }
        public TService? GetService<TService>(object? key = null) where TService : class => null;
        public object? GetService(Type serviceType, object? key = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "test")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
