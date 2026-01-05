// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests;

public class MetricRegistryTests
{
    [Fact]
    public void Register_ByName_AddsMetric()
    {
        var registry = new MetricRegistry();
        var metric = new TestMetric("test_metric");

        registry.Register("custom_name", metric);

        Assert.True(registry.Contains("custom_name"));
        Assert.Equal(metric, registry.Get("custom_name"));
    }

    [Fact]
    public void Register_ByMetricName_AddsMetric()
    {
        var registry = new MetricRegistry();
        var metric = new TestMetric("test_metric");

        registry.Register(metric);

        Assert.True(registry.Contains("test_metric"));
        Assert.Equal(metric, registry.Get("test_metric"));
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var registry = new MetricRegistry();
        var metric1 = new TestMetric("test");
        var metric2 = new TestMetric("test");

        registry.Register(metric1);

        Assert.Throws<InvalidOperationException>(() => registry.Register(metric2));
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var registry = new MetricRegistry();

        var result = registry.Get("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void GetRequired_NonExistent_Throws()
    {
        var registry = new MetricRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("nonexistent"));
        Assert.Contains("nonexistent", ex.Message);
    }

    [Fact]
    public void GetRequired_Exists_ReturnsMetric()
    {
        var registry = new MetricRegistry();
        var metric = new TestMetric("test");
        registry.Register(metric);

        var result = registry.GetRequired("test");

        Assert.Equal(metric, result);
    }

    [Fact]
    public void Contains_CaseInsensitive()
    {
        var registry = new MetricRegistry();
        registry.Register(new TestMetric("TestMetric"));

        Assert.True(registry.Contains("testmetric"));
        Assert.True(registry.Contains("TESTMETRIC"));
        Assert.True(registry.Contains("TestMetric"));
    }

    [Fact]
    public void GetRegisteredNames_ReturnsAllNames()
    {
        var registry = new MetricRegistry();
        registry.Register(new TestMetric("metric1"));
        registry.Register(new TestMetric("metric2"));
        registry.Register(new TestMetric("metric3"));

        var names = registry.GetRegisteredNames().ToList();

        Assert.Equal(3, names.Count);
        Assert.Contains("metric1", names);
        Assert.Contains("metric2", names);
        Assert.Contains("metric3", names);
    }

    [Fact]
    public void GetAll_ReturnsAllMetrics()
    {
        var registry = new MetricRegistry();
        var metric1 = new TestMetric("metric1");
        var metric2 = new TestMetric("metric2");
        registry.Register(metric1);
        registry.Register(metric2);

        var all = registry.GetAll().ToList();

        Assert.Equal(2, all.Count);
        Assert.Contains(metric1, all);
        Assert.Contains(metric2, all);
    }

    [Fact]
    public void Remove_ExistingMetric_ReturnsTrue()
    {
        var registry = new MetricRegistry();
        registry.Register(new TestMetric("test"));

        var removed = registry.Remove("test");

        Assert.True(removed);
        Assert.False(registry.Contains("test"));
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        var registry = new MetricRegistry();

        var removed = registry.Remove("nonexistent");

        Assert.False(removed);
    }

    [Fact]
    public void Clear_RemovesAllMetrics()
    {
        var registry = new MetricRegistry();
        registry.Register(new TestMetric("metric1"));
        registry.Register(new TestMetric("metric2"));

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.Empty(registry.GetRegisteredNames());
    }

    [Fact]
    public void Constructor_WithMetrics_PrePopulates()
    {
        var metrics = new[]
        {
            new TestMetric("metric1"),
            new TestMetric("metric2")
        };

        var registry = new MetricRegistry(metrics);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.Contains("metric1"));
        Assert.True(registry.Contains("metric2"));
    }

    private class TestMetric : IMetric
    {
        public TestMetric(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public string Description => "Test metric";

        public Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MetricResult.Pass(Name, 1.0, "Test passed"));
        }
    }
}
