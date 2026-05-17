// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Tests for the <see cref="LongMemEvalBenchmark"/> façade and its preset configuration.
/// </summary>
/// <remarks>
/// Phase 7 (v0.10.0-beta). These tests verify the factory-method contract — that the
/// presets return correctly-configured <see cref="LongMemEvalBenchmarkRunner"/> instances
/// and that the façade lives in the right namespace/shape. They do NOT run the actual
/// benchmark (the full LongMemEval dataset is not bundled with the repo, and a real run
/// would require LLM calls).
/// </remarks>
public class LongMemEvalBenchmarkTests
{
    // ─── Preset shape ─────────────────────────────────────────────────────────

    [Fact]
    public void Subset_ReturnsNonNull_LongMemEvalBenchmarkRunner()
    {
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Subset(client);

        Assert.NotNull(runner);
        Assert.IsType<LongMemEvalBenchmarkRunner>(runner);
    }

    [Fact]
    public void Full_ReturnsNonNull_LongMemEvalBenchmarkRunner()
    {
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Full(client);

        Assert.NotNull(runner);
        Assert.IsType<LongMemEvalBenchmarkRunner>(runner);
    }

    [Fact]
    public void Subset_And_Full_ReturnDistinctRunnerInstances()
    {
        var client = new StubChatClient();
        var subset = LongMemEvalBenchmark.Subset(client);
        var full = LongMemEvalBenchmark.Full(client);

        // The two presets must return separate runner instances (different configuration
        // semantics — subset uses the embedded resource, full targets the external dataset).
        Assert.NotSame(subset, full);
    }

    // ─── Preset options contract ───────────────────────────────────────────────

    [Fact]
    public void SubsetOptions_HasMaxQuestions_Set()
    {
        var opts = LongMemEvalBenchmark.SubsetOptions;

        Assert.NotNull(opts.MaxQuestions);
        Assert.True(opts.MaxQuestions > 0,
            "Subset preset must limit question count to something positive.");
        Assert.Equal("Subset", opts.DatasetMode);
        Assert.True(opts.StratifiedSampling,
            "Subset preset should use stratified sampling for reproducibility.");
    }

    [Fact]
    public void FullOptions_HasNoMaxQuestions_LimitAndFullDatasetMode()
    {
        var opts = LongMemEvalBenchmark.FullOptions;

        // Full preset runs the entire dataset — no artificial cap.
        Assert.Null(opts.MaxQuestions);
        Assert.Equal("Full", opts.DatasetMode);
        Assert.True(opts.StratifiedSampling);
    }

    [Fact]
    public void SubsetOptions_MaxQuestions_IsSet_FullOptions_IsUnbounded()
    {
        // Subset is bounded; Full is unbounded (null = run all).
        Assert.NotNull(LongMemEvalBenchmark.SubsetOptions.MaxQuestions);
        Assert.Null(LongMemEvalBenchmark.FullOptions.MaxQuestions);
    }

    // ─── Namespace / convention contract ──────────────────────────────────────

    [Fact]
    public void LongMemEvalBenchmark_LivesIn_AgentEvalBenchmarksNamespace()
    {
        // Convention 1 (ADR-017): every benchmark factory lives in AgentEval.Benchmarks,
        // regardless of which assembly implements it.
        Assert.Equal("AgentEval.Benchmarks", typeof(LongMemEvalBenchmark).Namespace);
    }

    [Fact]
    public void LongMemEvalBenchmark_IsStaticClass_AbstractAndSealed()
    {
        // C# static classes compile to abstract+sealed in IL.
        var t = typeof(LongMemEvalBenchmark);
        Assert.True(t.IsAbstract && t.IsSealed,
            $"{t.Name} must be a static class (abstract+sealed in IL); Convention 1 requires static partial.");
    }

    [Fact]
    public void LongMemEvalBenchmark_IsPublic()
    {
        Assert.True(typeof(LongMemEvalBenchmark).IsPublic,
            "LongMemEvalBenchmark must be public for API consumers.");
    }

    // ─── Null-guard contract ───────────────────────────────────────────────────

    [Fact]
    public void Subset_NullChatClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LongMemEvalBenchmark.Subset(null!));
    }

    [Fact]
    public void Full_NullChatClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LongMemEvalBenchmark.Full(null!));
    }

    // ─── Runner identity ───────────────────────────────────────────────────────

    [Fact]
    public void Runner_BenchmarkId_Is_LongMemEval()
    {
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Subset(client);

        // The runner's BenchmarkId must remain "longmemeval" so CLI and Mission Control
        // can identify it across preset variants.
        Assert.Equal("longmemeval", runner.BenchmarkId);
    }

    [Fact]
    public void Runner_DisplayName_ContainsLongMemEval()
    {
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Full(client);

        Assert.Contains("LongMemEval", runner.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IChatClient"/> stub for façade construction tests.
    /// Never called during these tests — the runner is constructed but not executed.
    /// </summary>
    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "stub")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not used in construction tests.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
