// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.DependencyInjection;
using AgentEval.Exporters;
using AgentEval.Models;
using AgentEval.RedTeam;
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

    [Fact]
    public void AddAgentEvalDataLoaders_RegistersDatasetLoaderFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var factory = provider.GetRequiredService<IDatasetLoaderFactory>();

        // Assert - registered as singleton DefaultDatasetLoaderFactory
        Assert.NotNull(factory);
        Assert.IsType<DefaultDatasetLoaderFactory>(factory);

        // Verify same instance (singleton)
        var factory2 = provider.GetRequiredService<IDatasetLoaderFactory>();
        Assert.Same(factory, factory2);

        // Verify it works
        var loader = factory.CreateFromExtension(".jsonl");
        Assert.Equal("jsonl", loader.Format);
    }

    [Fact]
    public void AddAgentEval_RegistersMetricRegistry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IMetricRegistry>();

        // Assert
        Assert.NotNull(registry);
        Assert.IsType<MetricRegistry>(registry);
    }

    [Fact]
    public void AddAgentEval_MetricRegistry_PopulatesFromDIRegisteredMetrics()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IMetric>(new FakeMetric("test_metric_1"));
        services.AddSingleton<IMetric>(new FakeMetric("test_metric_2"));
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IMetricRegistry>();

        // Assert — both DI-registered metrics should be in the registry
        Assert.True(registry.Contains("test_metric_1"));
        Assert.True(registry.Contains("test_metric_2"));
    }

    [Fact]
    public void AddAgentEval_ExistingMetricRegistry_NotOverridden()
    {
        // Arrange
        var services = new ServiceCollection();
        var customRegistry = new MetricRegistry();
        customRegistry.Register(new FakeMetric("custom_only"));
        services.AddSingleton<IMetricRegistry>(customRegistry);
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IMetricRegistry>();

        // Assert — TryAdd should not override existing registration
        Assert.Same(customRegistry, registry);
        Assert.True(registry.Contains("custom_only"));
    }

    [Fact]
    public void AddAgentEvalDataLoaders_RegistersExporterRegistry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IExporterRegistry>();

        // Assert
        Assert.NotNull(registry);
    }

    [Fact]
    public void AddAgentEvalDataLoaders_ExporterRegistry_ContainsBuiltInExporters()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IExporterRegistry>();

        // Assert — 5 built-in exporters
        Assert.True(registry.Contains("Json"));
        Assert.True(registry.Contains("Junit"));
        Assert.True(registry.Contains("Markdown"));
        Assert.True(registry.Contains("Csv"));
        Assert.True(registry.Contains("Trx"));
    }

    [Fact]
    public void AddAgentEvalDataLoaders_ExporterRegistry_PopulatesFromDIRegisteredExporters()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IResultExporter>(new FakeExporter("powerbi", ".pbix"));
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IExporterRegistry>();

        // Assert — includes both built-in and custom
        Assert.True(registry.Contains("powerbi"));
        Assert.True(registry.Contains("Json")); // still has built-ins
    }

    [Fact]
    public void AddAgentEvalAll_RegistersAttackTypeRegistry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddAgentEvalAll();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IAttackTypeRegistry>();

        // Assert — all 9 built-in attacks
        Assert.NotNull(registry);
        Assert.True(registry.Contains("PromptInjection"));
        Assert.True(registry.Contains("Jailbreak"));
        Assert.True(registry.Contains("PIILeakage"));
    }

    [Fact]
    public void AddAgentEvalAll_AttackTypeRegistry_PopulatesFromDIRegisteredAttacks()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IAttackType>(new FakeAttackType("CustomSqlInjection", "LLM99"));
        services.AddAgentEvalAll();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IAttackTypeRegistry>();

        // Assert — includes both built-in and custom
        Assert.True(registry.Contains("CustomSqlInjection"));
        Assert.True(registry.Contains("PromptInjection")); // still has built-ins
    }

    [Fact]
    public void AddAgentEvalDataLoaders_ExistingExporterRegistry_NotOverridden()
    {
        // Arrange — register custom IExporterRegistry before AddAgentEvalDataLoaders
        var services = new ServiceCollection();
        var customRegistry = new ExporterRegistry();
        customRegistry.Register("custom", new FakeExporter("custom", ".custom"));
        services.AddSingleton<IExporterRegistry>(customRegistry);
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IExporterRegistry>();

        // Assert — TryAdd should not override existing registration
        Assert.Same(customRegistry, registry);
        Assert.True(registry.Contains("custom"));
    }

    [Fact]
    public void AddAgentEvalAll_ExistingAttackTypeRegistry_NotOverridden()
    {
        // Arrange — register custom IAttackTypeRegistry before AddAgentEvalAll
        var services = new ServiceCollection();
        var customRegistry = new AttackTypeRegistry();
        customRegistry.Register(new FakeAttackType("CustomOnly", "LLM99"));
        services.AddSingleton<IAttackTypeRegistry>(customRegistry);
        services.AddAgentEvalAll();
        var provider = services.BuildServiceProvider();

        // Act
        var registry = provider.GetRequiredService<IAttackTypeRegistry>();

        // Assert — TryAdd should not override existing registration
        Assert.Same(customRegistry, registry);
        Assert.True(registry.Contains("CustomOnly"));
    }

    [Fact]
    public void AddAgentEvalDataLoaders_DatasetLoaderFactory_AutoWiresDILoaders()
    {
        // Arrange
        var services = new ServiceCollection();
        var customLoader = new FakeDatasetLoader("parquet", [".parquet"]);
        services.AddSingleton<IDatasetLoader>(customLoader);
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var factory = provider.GetRequiredService<IDatasetLoaderFactory>();
        var loader = factory.CreateFromExtension(".parquet");

        // Assert — custom loader was auto-wired
        Assert.Same(customLoader, loader);
    }

    [Fact]
    public void AddAgentEvalDataLoaders_DatasetLoaderFactory_BuiltInsNotOverridden()
    {
        // Arrange
        var services = new ServiceCollection();
        var customJsonLoader = new FakeDatasetLoader("json", [".json"]);
        services.AddSingleton<IDatasetLoader>(customJsonLoader);
        services.AddAgentEvalDataLoaders();
        var provider = services.BuildServiceProvider();

        // Act
        var factory = provider.GetRequiredService<IDatasetLoaderFactory>();
        var loader = factory.CreateFromExtension(".json");

        // Assert — built-in loader should NOT be overridden by DI loader
        Assert.NotSame(customJsonLoader, loader);
    }

    [Fact]
    public void AddAgentEval_RegistersEvaluator_WhenChatClientAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        var fakeChatClient = new AgentEval.Testing.FakeChatClient("test response");
        services.AddSingleton<IChatClient>(fakeChatClient);
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var evaluator = provider.GetRequiredService<IEvaluator>();

        // Assert
        Assert.NotNull(evaluator);
        Assert.IsType<ChatClientEvaluator>(evaluator);
    }

    [Fact]
    public void AddAgentEval_EvaluatorThrows_WhenNoChatClient()
    {
        // Arrange — no IChatClient registered
        var services = new ServiceCollection();
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act & Assert — resolution should throw because IChatClient is missing
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEvaluator>());
    }

    [Fact]
    public void AddAgentEval_ExistingEvaluator_NotOverridden()
    {
        // Arrange — register custom IEvaluator before AddAgentEval
        var services = new ServiceCollection();
        var customEvaluator = new FakeEvaluator();
        services.AddSingleton<IEvaluator>(customEvaluator);
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var evaluator = provider.GetRequiredService<IEvaluator>();

        // Assert — TryAdd should not override
        Assert.Same(customEvaluator, evaluator);
    }

    [Fact]
    public void AddAgentEval_EmbeddingsThrows_WhenNoEmbeddingGenerator()
    {
        // Arrange — no IEmbeddingGenerator registered
        var services = new ServiceCollection();
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act & Assert — resolution should throw because IEmbeddingGenerator is missing
        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<AgentEval.Embeddings.IAgentEvalEmbeddings>());
    }

    [Fact]
    public void AddAgentEval_ExistingEmbeddings_NotOverridden()
    {
        // Arrange — register custom IAgentEvalEmbeddings before AddAgentEval
        var services = new ServiceCollection();
        var customEmbeddings = new FakeEmbeddings();
        services.AddSingleton<AgentEval.Embeddings.IAgentEvalEmbeddings>(customEmbeddings);
        services.AddAgentEval();
        var provider = services.BuildServiceProvider();

        // Act
        var embeddings = provider.GetRequiredService<AgentEval.Embeddings.IAgentEvalEmbeddings>();

        // Assert — TryAdd should not override
        Assert.Same(customEmbeddings, embeddings);
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

    private class FakeMetric(string name) : IMetric
    {
        public string Name => name;
        public string Description => $"Fake metric: {name}";

        public Task<MetricResult> EvaluateAsync(
            EvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MetricResult { MetricName = name, Score = 1.0, Passed = true });
        }
    }

    private class FakeExporter(string formatName, string extension) : IResultExporter
    {
        public ExportFormat Format => ExportFormat.Json; // Doesn't matter for custom
        public string FormatName => formatName;
        public string FileExtension => extension;
        public string ContentType => "application/octet-stream";

        public Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }

    private class FakeDatasetLoader(string format, IReadOnlyList<string> extensions) : IDatasetLoader
    {
        public string Format => format;
        public IReadOnlyList<string> SupportedExtensions => extensions;
        public bool IsTrulyStreaming => false;

        public Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(string path, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }

    private class FakeAttackType(string name, string owaspId) : IAttackType
    {
        public string Name => name;
        public string DisplayName => name;
        public string Description => $"Fake attack: {name}";
        public string OwaspLlmId => owaspId;
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Medium;

        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [];
        public IProbeEvaluator GetEvaluator() => throw new NotImplementedException();
    }

    private class FakeEvaluator : IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentEval.Core.EvaluationResult { OverallScore = 100 });
        }
    }

    private class FakeEmbeddings : AgentEval.Embeddings.IAgentEvalEmbeddings
    {
        public Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
            string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1.0f, 0.0f });
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
            IEnumerable<string> texts, CancellationToken cancellationToken = default)
        {
            var results = texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1.0f, 0.0f }).ToList();
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
        }
    }
}
