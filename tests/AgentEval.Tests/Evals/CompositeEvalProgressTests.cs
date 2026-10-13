// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Evals;

public sealed class CompositeEvalProgressTests
{
    private sealed class Leaf(Func<CancellationToken, Task> action) : AtomicEval("leaf", "Leaf", "test", "1")
    {
        public override async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            await action(ct);
            return new EvalResult(new(Key, Name, Category, Version), new(1, null, "pass", true, null, "none", null),
                new(null, null, null, null, null), new("atomic-code", null, null, null, null, 0, false),
                DateTimeOffset.UtcNow);
        }
    }

    private static CompositeEval Tree(params IEval[] children) => new("tree", "Tree", "test", "1",
        children.Select(c => new EvalComponent(c)).ToArray(), WeightedSumAggregation.Instance);

    [Fact]
    public async Task EvaluateWithProgressAsync_ReusedNodes_ReportsDistinctPathsAndSameResults()
    {
        var leaf = new Leaf(_ => Task.CompletedTask);
        var tree = Tree(Tree(leaf, leaf), leaf);
        var observed = new ConcurrentDictionary<string, EvalResult>();
        var started = new ConcurrentDictionary<string, bool>();

        var result = await tree.EvaluateWithProgressAsync(new("query"),
            (path, node) =>
            {
                Assert.True(started.ContainsKey(string.Join('/', path)));
                Assert.True(observed.TryAdd(string.Join('/', path), node));
            }, onStarted: path => Assert.True(started.TryAdd(string.Join('/', path), true)));

        Assert.Equal(5, observed.Count);
        Assert.Equal(5, started.Count);
        Assert.Same(result, observed[""]);
        Assert.Same(result.Details.SubResults![0], observed["0"]);
        Assert.Same(result.Details.SubResults[0].Details.SubResults![0], observed["0/0"]);
        Assert.Same(result.Details.SubResults[0].Details.SubResults![1], observed["0/1"]);
        Assert.Same(result.Details.SubResults[1], observed["1"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvaluateWithProgressAsync_InterruptedTree_RetainsCompletedDescendants(bool fault)
    {
        using var cancel = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var good = new Leaf(_ => Task.CompletedTask);
        var interrupted = new Leaf(async ct =>
        {
            await finished.Task.WaitAsync(ct);
            if (fault) throw new InvalidOperationException("test fault");
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        var tree = Tree(Tree(good, interrupted));
        var observed = new ConcurrentDictionary<string, EvalResult>();
        var task = tree.EvaluateWithProgressAsync(new("query"), (path, node) =>
        {
            observed.TryAdd(string.Join('/', path), node);
            finished.TrySetResult();
        }, cancel.Token);

        if (fault) await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal("0/0", Assert.Single(observed).Key);
    }

    [Fact]
    public async Task EvaluateWithProgressAsync_CancelledBeforeStart_DoesNotInvokeNodesOrCallbacks()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var calls = 0;
        var tree = Tree(new Leaf(_ => { calls++; return Task.CompletedTask; }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tree.EvaluateWithProgressAsync(new("query"),
            (_, _) => calls++, cancel.Token, _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EvaluateWithProgressAsync_ConcurrentInvocations_DoNotMixObservers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = Tree(new Leaf(ct => release.Task.WaitAsync(ct)));
        var first = new ConcurrentBag<EvalResult>();
        var second = new ConcurrentBag<EvalResult>();
        var a = tree.EvaluateWithProgressAsync(new("a"), (_, result) => first.Add(result));
        var b = tree.EvaluateWithProgressAsync(new("b"), (_, result) => second.Add(result));
        release.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Contains(results[0], first);
        Assert.Contains(results[1], second);
        Assert.DoesNotContain(results[0], second);
    }
}
