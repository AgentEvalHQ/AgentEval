// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests;

public class AgentEvalBuilderTests
{
    [Fact]
    public void Create_ReturnsNewBuilder()
    {
        var builder = AgentEvalBuilder.Create();

        Assert.NotNull(builder);
    }

    [Fact]
    public async Task Build_WithMetrics_RegistersInRegistry()
    {
        var metric = new TestMetric("test_metric");

        await using var runner = await AgentEvalBuilder.Create()
            .AddMetric(metric)
            .WithNoLogging()
            .BuildAsync();

        Assert.True(runner.Metrics.Contains("test_metric"));
    }

    [Fact]
    public async Task Build_WithMultipleMetrics_RegistersAll()
    {
        var metric1 = new TestMetric("metric1");
        var metric2 = new TestMetric("metric2");

        await using var runner = await AgentEvalBuilder.Create()
            .AddMetrics(metric1, metric2)
            .WithNoLogging()
            .BuildAsync();

        Assert.True(runner.Metrics.Contains("metric1"));
        Assert.True(runner.Metrics.Contains("metric2"));
    }

    [Fact]
    public async Task WithDefaultThreshold_OutOfRange_Throws()
    {
        var builder = AgentEvalBuilder.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithDefaultThreshold(1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithDefaultThreshold(-0.1));
    }

    [Fact]
    public async Task Configure_SetsConfiguration()
    {
        var plugin = new TestPlugin();

        await using var runner = await AgentEvalBuilder.Create()
            .AddPlugin(plugin)
            .Configure("test_key", "test_value")
            .WithNoLogging()
            .BuildAsync();

        Assert.Equal("test_value", plugin.ReceivedConfig);
    }

    [Fact]
    public async Task AddPlugin_InitializesPlugin()
    {
        var plugin = new TestPlugin();

        await using var runner = await AgentEvalBuilder.Create()
            .AddPlugin(plugin)
            .WithNoLogging()
            .BuildAsync();

        Assert.True(plugin.WasInitialized);
        Assert.Equal(PluginLifecycleStage.Ready, plugin.Stage);
    }

    [Fact]
    public async Task EvaluateAsync_RunsMetricAndHooks()
    {
        var metric = new TestMetric("test");
        var plugin = new TestPlugin();

        await using var runner = await AgentEvalBuilder.Create()
            .AddMetric(metric)
            .AddPlugin(plugin)
            .WithNoLogging()
            .BuildAsync();

        var result = await runner.EvaluateAsync("test", "input", "output");

        Assert.True(result.Passed);
        Assert.Equal("test", result.MetricName);
        Assert.True(plugin.BeforeEvaluationCalled);
        Assert.True(plugin.AfterEvaluationCalled);
    }

    [Fact]
    public async Task EvaluateAllAsync_RunsAllMetrics()
    {
        var metric1 = new TestMetric("metric1");
        var metric2 = new TestMetric("metric2");

        await using var runner = await AgentEvalBuilder.Create()
            .AddMetrics(metric1, metric2)
            .WithNoLogging()
            .BuildAsync();

        var results = await runner.EvaluateAllAsync("input", "output");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task DisposeAsync_ShutsDownPlugins()
    {
        var plugin = new TestPlugin();

        var runner = await AgentEvalBuilder.Create()
            .AddPlugin(plugin)
            .WithNoLogging()
            .BuildAsync();

        await runner.DisposeAsync();

        Assert.True(plugin.WasShutdown);
        Assert.Equal(PluginLifecycleStage.Disposed, plugin.Stage);
    }

    [Fact]
    public async Task AddTransformer_TransformsResults()
    {
        var metric = new TestMetric("test", score: 0.5);
        var transformer = new ScoreDoublerTransformer();

        await using var runner = await AgentEvalBuilder.Create()
            .AddMetric(metric)
            .AddTransformer(transformer)
            .WithNoLogging()
            .BuildAsync();

        var result = await runner.EvaluateAsync("test", "input", "output");

        Assert.Equal(1.0, result.Score); // 0.5 * 2 = 1.0
    }

    [Fact]
    public async Task Build_Synchronous_Works()
    {
        var metric = new TestMetric("test");

        var runner = AgentEvalBuilder.Create()
            .AddMetric(metric)
            .WithNoLogging()
            .Build();

        Assert.NotNull(runner);
        Assert.True(runner.Metrics.Contains("test"));

        await runner.DisposeAsync();
    }

    private class TestMetric : IMetric
    {
        private readonly double _score;

        public TestMetric(string name, double score = 1.0)
        {
            Name = name;
            _score = score;
        }

        public string Name { get; }
        public string Description => "Test metric";

        public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MetricResult.Pass(Name, _score, "Test passed"));
        }
    }

    private class TestPlugin : AgentEvalPluginBase
    {
        public override string PluginId => "test_plugin";
        public override string Name => "Test Plugin";

        public bool WasInitialized { get; private set; }
        public bool WasShutdown { get; private set; }
        public bool BeforeEvaluationCalled { get; private set; }
        public bool AfterEvaluationCalled { get; private set; }
        public string? ReceivedConfig { get; private set; }

        protected override Task OnInitializeAsync(CancellationToken cancellationToken)
        {
            WasInitialized = true;
            ReceivedConfig = Context?.GetConfig<string>("test_key");
            return Task.CompletedTask;
        }

        public override Task OnBeforeEvaluationAsync(EvaluationContext context, CancellationToken cancellationToken = default)
        {
            BeforeEvaluationCalled = true;
            return Task.CompletedTask;
        }

        public override Task OnAfterEvaluationAsync(EvaluationContext context, IList<MetricResult> results, CancellationToken cancellationToken = default)
        {
            AfterEvaluationCalled = true;
            return Task.CompletedTask;
        }

        protected override Task OnShutdownAsync(CancellationToken cancellationToken)
        {
            WasShutdown = true;
            return Task.CompletedTask;
        }
    }

    private class ScoreDoublerTransformer : IResultTransformer
    {
        public int Priority => 0;

        public MetricResult Transform(MetricResult result, EvaluationContext context)
        {
            var newScore = Math.Min(result.Score * 2, 100);
            return MetricResult.Pass(result.MetricName, newScore, result.Explanation);
        }
    }
}
