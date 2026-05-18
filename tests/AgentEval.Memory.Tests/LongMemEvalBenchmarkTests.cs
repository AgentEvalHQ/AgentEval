// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
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
        // Phase 8 (v0.10.0-beta): Full() now requires LONGMEMEVAL_DATASET_PATH.
        // Set a fake path for construction-only tests (the runner won't actually
        // read the file in this test).
        using var _ = SetEnvVar("LONGMEMEVAL_DATASET_PATH", "/fake/path/to/longmemeval-s.json");
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Full(client);

        Assert.NotNull(runner);
        Assert.IsType<LongMemEvalBenchmarkRunner>(runner);
    }

    [Fact]
    public void Subset_And_Full_ReturnDistinctRunnerInstances()
    {
        using var _ = SetEnvVar("LONGMEMEVAL_DATASET_PATH", "/fake/path/to/longmemeval-s.json");
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

    // ─── Phase 8 (v0.10.0-beta) follow-ups from Phase 7 gate review ────────────

    /// <summary>
    /// Phase 8 follow-up #2: Full() must throw a clear InvalidOperationException
    /// when LONGMEMEVAL_DATASET_PATH is unset, rather than silently degrading to the
    /// 30-question embedded subset.
    /// </summary>
    [Fact]
    public void Full_WhenEnvVarUnset_Throws_InvalidOperationException()
    {
        var client = new StubChatClient();
        var oldValue = Environment.GetEnvironmentVariable("LONGMEMEVAL_DATASET_PATH");
        Environment.SetEnvironmentVariable("LONGMEMEVAL_DATASET_PATH", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => LongMemEvalBenchmark.Full(client));
            Assert.Contains("LONGMEMEVAL_DATASET_PATH", ex.Message);
            Assert.Contains("Subset", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LONGMEMEVAL_DATASET_PATH", oldValue);
        }
    }

    /// <summary>
    /// Phase 8 follow-up #1: Subset() now bakes SubsetOptions into the returned runner's
    /// DefaultOptions so the preset's intended sampling cap and random seed are actually
    /// applied (closes the Phase 7 dead-RandomSeed footgun).
    /// v0.10.1-beta: SubsetMaxQuestions raised from 30 → 50 to reflect "representative sample
    /// of the real 500" intent (the embedded 30Q fake subset was deleted).
    /// </summary>
    [Fact]
    public void Subset_RunnerHasDefaultOptionsBakedIn()
    {
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Subset(client);

        Assert.NotNull(runner.DefaultOptions);
        Assert.Equal(LongMemEvalBenchmark.SubsetMaxQuestions, runner.DefaultOptions!.MaxQuestions);
        Assert.Equal(50, runner.DefaultOptions.MaxQuestions);
        Assert.Equal(42, runner.DefaultOptions.RandomSeed);
        Assert.Equal("Subset", runner.DefaultOptions.DatasetMode);
        Assert.True(runner.DefaultOptions.StratifiedSampling);
    }

    /// <summary>
    /// Phase 8 follow-up #4: round-trip serialization of ExternalBenchmarkResult through
    /// System.Text.Json must preserve all top-level metric fields. This catches accidental
    /// `[JsonIgnore]` regressions on the result record.
    /// </summary>
    [Fact]
    public void ExternalBenchmarkResult_RoundTripSerialization_PreservesMetrics()
    {
        var original = new ExternalBenchmarkResult
        {
            BenchmarkId = "longmemeval",
            BenchmarkName = "LongMemEval (ICLR 2025)",
            OverallAccuracy = 73.5,
            TaskAveragedAccuracy = 71.2,
            PerTypeResults = new Dictionary<string, TypeResult>
            {
                ["temporal-reasoning"] = new TypeResult
                {
                    TypeName = "temporal-reasoning",
                    TotalQuestions = 5,
                    CorrectQuestions = 4,
                    Duration = TimeSpan.FromSeconds(15),
                },
            },
            QuestionResults = new List<QuestionResult>
            {
                new QuestionResult
                {
                    QuestionId = "q-1",
                    QuestionType = "temporal-reasoning",
                    Question = "What date did the user mention?",
                    GoldAnswer = "2024-01-15",
                    AgentResponse = "January 15, 2024",
                    Correct = true,
                    RawScore = 1.0,
                    JudgeExplanation = "Date matches",
                    Duration = TimeSpan.FromSeconds(3),
                },
            },
            Duration = TimeSpan.FromMinutes(2),
            TotalLlmCalls = 60,
            EstimatedCostUsd = 0.024,
            Options = new ExternalBenchmarkOptions
            {
                MaxQuestions = 30,
                StratifiedSampling = true,
                RandomSeed = 42,
                PreserveSessionBoundaries = true,
                IncludeTimestamps = true,
                DatasetMode = "Subset"
            }
        };

        var json = JsonSerializer.Serialize(original);
        var rehydrated = JsonSerializer.Deserialize<ExternalBenchmarkResult>(json);

        Assert.NotNull(rehydrated);
        Assert.Equal(original.BenchmarkId, rehydrated!.BenchmarkId);
        Assert.Equal(original.BenchmarkName, rehydrated.BenchmarkName);
        Assert.Equal(original.OverallAccuracy, rehydrated.OverallAccuracy);
        Assert.Equal(original.TaskAveragedAccuracy, rehydrated.TaskAveragedAccuracy);
        Assert.Equal(original.TotalLlmCalls, rehydrated.TotalLlmCalls);
        Assert.Equal(original.EstimatedCostUsd, rehydrated.EstimatedCostUsd);
        Assert.Equal(original.Duration, rehydrated.Duration);
        Assert.Single(rehydrated.PerTypeResults);
        Assert.Single(rehydrated.QuestionResults);
        Assert.Equal("Subset", rehydrated.Options.DatasetMode);
    }

    /// <summary>
    /// v0.10.1-beta: the fake "embedded subset" was removed. When the loader cannot
    /// locate the real dataset (explicit path -> env var -> canonical local path),
    /// it must throw <see cref="LongMemEvalDatasetNotFoundException"/> with the
    /// download URL + env-var name baked into the message so users see exactly how
    /// to recover. This test simulates the "nothing on disk" path by pointing the
    /// env var at a path we know doesn't exist (and using a non-existent canonical
    /// dataset would require deleting the file on disk — which we don't want to do
    /// in a test).
    /// </summary>
    [Fact]
    public void LongMemEvalDataLoader_LoadFromFile_MissingPath_Throws_DatasetNotFound()
    {
        var opts = LongMemEvalBenchmark.SubsetOptions;
        var bogus = Path.Combine(Path.GetTempPath(), $"agenteval-longmemeval-missing-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<LongMemEvalDatasetNotFoundException>(
            () => LongMemEvalDataLoader.LoadFromFile(bogus, opts));
        Assert.Contains(LongMemEvalDataLoader.DatasetPathEnvVar, ex.Message);
        Assert.Contains(LongMemEvalDataLoader.DatasetDownloadUrl, ex.Message);
    }

    /// <summary>
    /// v0.10.1-beta: the dataset-not-found exception subclasses
    /// <see cref="FileNotFoundException"/> so existing <c>catch (FileNotFoundException)</c>
    /// blocks still trigger, while the concrete type enables the sample's friendly box.
    /// </summary>
    [Fact]
    public void LongMemEvalDatasetNotFoundException_Subclasses_FileNotFoundException()
    {
        Assert.True(typeof(FileNotFoundException).IsAssignableFrom(typeof(LongMemEvalDatasetNotFoundException)),
            "LongMemEvalDatasetNotFoundException must subclass FileNotFoundException for back-compat.");
    }

    /// <summary>
    /// PR #30 review follow-up: a caller-supplied <c>explicitPath</c> that points
    /// at a non-existent file MUST throw, not silently fall through to the env
    /// var or canonical local default. Otherwise a typo in <c>DatasetPath</c>
    /// produces a benchmark run against a different dataset than the caller
    /// asked for, which is a misleading-results bug.
    /// </summary>
    [Fact]
    public void ResolveDatasetPath_ExplicitMissingPath_ThrowsDatasetNotFound_DoesNotFallThrough()
    {
        // Clear the env var so a fall-through, if it happened, would land on the
        // canonical local path. We're asserting the throw fires BEFORE that.
        using var _ = SetEnvVar("LONGMEMEVAL_DATASET_PATH", null);
        var bogus = Path.Combine(Path.GetTempPath(), $"agenteval-longmemeval-explicit-missing-{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<LongMemEvalDatasetNotFoundException>(
            () => LongMemEvalDataLoader.ResolveDatasetPath(bogus));

        // Exception message must mention the path the caller actually asked for —
        // not the canonical path — so the user can fix their typo.
        Assert.Contains(bogus, ex.Message);
    }

    /// <summary>
    /// PR #30 review follow-up: the <c>LONGMEMEVAL_DATASET_PATH</c> env var is
    /// the other "explicit declaration of intent" (it is required by
    /// <c>LongMemEvalBenchmark.Full()</c>). A typo in that env var must surface
    /// the same way — throw rather than silently load the canonical dataset.
    /// </summary>
    [Fact]
    public void ResolveDatasetPath_EnvVarMissingPath_ThrowsDatasetNotFound_DoesNotFallThrough()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"agenteval-longmemeval-envvar-missing-{Guid.NewGuid():N}.json");
        using var _ = SetEnvVar("LONGMEMEVAL_DATASET_PATH", bogus);

        var ex = Assert.Throws<LongMemEvalDatasetNotFoundException>(
            () => LongMemEvalDataLoader.ResolveDatasetPath(explicitPath: null));

        Assert.Contains(bogus, ex.Message);
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
        using var _ = SetEnvVar("LONGMEMEVAL_DATASET_PATH", "/fake/path/to/longmemeval-s.json");
        var client = new StubChatClient();
        var runner = LongMemEvalBenchmark.Full(client);

        Assert.Contains("LongMemEval", runner.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    /// <summary>Scoped environment variable setter that restores the prior value on Dispose().</summary>
    private static IDisposable SetEnvVar(string name, string? value)
    {
        var prev = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new ScopedDisposable(() => Environment.SetEnvironmentVariable(name, prev));
    }

    private sealed class ScopedDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public ScopedDisposable(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() => _onDispose();
    }

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
