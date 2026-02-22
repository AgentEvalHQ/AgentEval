// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.DependencyInjection;
using AgentEval.Models;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentEval.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAgentEval_RegistersCoreServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Assert
        Assert.NotNull(provider.GetService<IStatisticsCalculator>());
        Assert.NotNull(provider.GetService<IToolUsageExtractor>());
        Assert.NotNull(provider.GetService<IStochasticRunner>());
        Assert.NotNull(provider.GetService<IModelComparer>());
    }

    [Fact]
    public void AddAgentEvalScoped_RegistersServicesAsScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEvalScoped();
        var provider = services.BuildServiceProvider();

        // Assert - Create two scopes and verify instances are different between scopes
        using (var scope1 = provider.CreateScope())
        using (var scope2 = provider.CreateScope())
        {
            var runner1 = scope1.ServiceProvider.GetRequiredService<IStochasticRunner>();
            var runner2 = scope2.ServiceProvider.GetRequiredService<IStochasticRunner>();

            // Different instances between scopes
            Assert.NotSame(runner1, runner2);
        }

        // But same instance within a scope
        using (var scope = provider.CreateScope())
        {
            var runner1 = scope.ServiceProvider.GetRequiredService<IStochasticRunner>();
            var runner2 = scope.ServiceProvider.GetRequiredService<IStochasticRunner>();
            Assert.Same(runner1, runner2);
        }
    }

    [Fact]
    public void AddAgentEvalSingleton_RegistersServicesAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEvalSingleton();
        var provider = services.BuildServiceProvider();

        // Assert - Same instance across different requests
        var runner1 = provider.GetRequiredService<IStochasticRunner>();
        var runner2 = provider.GetRequiredService<IStochasticRunner>();

        Assert.Same(runner1, runner2);
    }

    [Fact]
    public void AddAgentEvalTransient_RegistersServicesAsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEvalTransient();
        var provider = services.BuildServiceProvider();

        // Assert - Different instance on each request
        var runner1 = provider.GetRequiredService<IStochasticRunner>();
        var runner2 = provider.GetRequiredService<IStochasticRunner>();

        Assert.NotSame(runner1, runner2);
    }

    [Fact]
    public void AddAgentEval_WithCustomOptions_RegistersWithCorrectLifetime()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEval(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Singleton;
        });
        var provider = services.BuildServiceProvider();

        // Assert - Same instance across requests (singleton behavior)
        var comparer1 = provider.GetRequiredService<IModelComparer>();
        var comparer2 = provider.GetRequiredService<IModelComparer>();

        Assert.Same(comparer1, comparer2);
    }

    [Fact]
    public void AddAgentEval_WithEvaluationHarnessFactory_RegistersTestHarness()
    {
        // Arrange
        var services = new ServiceCollection();
        var mockHarness = new FakeTestHarness();

        // Act
        services.AddAgentEval(options =>
        {
            options.EvaluationHarnessFactory = _ => mockHarness;
        });
        var provider = services.BuildServiceProvider();

        // Assert
        var harness = provider.GetService<IEvaluationHarness>();
        Assert.NotNull(harness);
        Assert.Same(mockHarness, harness);
    }

    [Fact]
    public void AddAgentEval_UtilitiesRegisteredAsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency

        // Act
        services.AddAgentEvalScoped(); // Even with scoped, utilities should be singleton
        var provider = services.BuildServiceProvider();

        // Assert - Same instance for utilities
        var calc1 = provider.GetRequiredService<IStatisticsCalculator>();
        var calc2 = provider.GetRequiredService<IStatisticsCalculator>();
        Assert.Same(calc1, calc2);

        var extractor1 = provider.GetRequiredService<IToolUsageExtractor>();
        var extractor2 = provider.GetRequiredService<IToolUsageExtractor>();
        Assert.Same(extractor1, extractor2);
    }

    [Fact]
    public void ModelComparer_ResolvedWithDependencies()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var comparer = provider.GetRequiredService<IModelComparer>();

        // Assert - Should successfully resolve with all dependencies
        Assert.NotNull(comparer);
        Assert.IsType<ModelComparer>(comparer);
    }

    [Fact]
    public void StochasticRunner_ResolvedWithDependencies()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IEvaluationHarness, FakeTestHarness>(); // Required dependency
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var runner = provider.GetRequiredService<IStochasticRunner>();

        // Assert - Should successfully resolve with all dependencies
        Assert.NotNull(runner);
        Assert.IsType<StochasticRunner>(runner);
    }

    [Fact]
    public void AddAgentEval_RegistersCalibratedJudgeFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act — resolve the factory delegate
        var factory = provider.GetRequiredService<Func<IEnumerable<(string Name, IChatClient Client)>, CalibratedJudgeOptions?, ICalibratedJudge>>();

        // Assert — factory creates a working CalibratedJudge
        Assert.NotNull(factory);

        var judges = new (string, IChatClient)[] { ("TestModel", new FakeChatClient("{}")) };
        var judge = factory(judges, null);
        Assert.NotNull(judge);
        Assert.Single(judge.JudgeNames);
        Assert.Equal("TestModel", judge.JudgeNames[0]);
    }

    [Fact]
    public void AddAgentEval_RegistersCalibratedEvaluatorFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act — resolve the factory delegate (returns IEvaluator)
        var factory = provider.GetRequiredService<Func<IEnumerable<(string Name, IChatClient Client)>, CalibratedJudgeOptions?, IEvaluator>>();

        // Assert — factory creates a working CalibratedEvaluator
        Assert.NotNull(factory);

        var judges = new (string, IChatClient)[] { ("TestModel", new FakeChatClient("{}")) };
        var evaluator = factory(judges, null);
        Assert.NotNull(evaluator);
        Assert.IsType<CalibratedEvaluator>(evaluator);
    }

    // Fake test harness for testing
    private class FakeTestHarness : IEvaluationHarness
    {
        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent,
            TestCase testCase,
            EvaluationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
