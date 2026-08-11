// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// What a completed run reports about itself: realised composition, judge-call attribution, and
/// provenance. Every value here is asserted against the run that produced it, not against the options
/// that requested it.
/// </summary>
public class LongMemEvalRunReportingTests
{
    // ── Ask 2 proof: realised counts ──────────────────────────────────────────

    [Fact]
    public async Task Composition_CountsWhatActuallyRanByTypeAndAbstention()
    {
        using var dataset = MixedDataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path
        });

        var composition = result.Composition;
        Assert.NotNull(composition);
        Assert.Equal(result.SelectedQuestions, composition!.TotalQuestions);
        // 3 abstention out of 9 in the fixture below.
        Assert.Equal(9, composition.TotalQuestions);
        Assert.Equal(3, composition.AbstentionQuestions);
        Assert.Equal(6, composition.NonAbstentionQuestions);
        Assert.Equal(1.0 / 3, composition.RealisedAbstentionProportion!.Value, 10);

        Assert.Equal(2, composition.ByQuestionType.Count);
        Assert.Equal(6, composition.ByQuestionType["single-session-user"].TotalQuestions);
        Assert.Equal(2, composition.ByQuestionType["single-session-user"].AbstentionQuestions);
        Assert.Equal(4, composition.ByQuestionType["single-session-user"].NonAbstentionQuestions);
        Assert.Equal(3, composition.ByQuestionType["temporal-reasoning"].TotalQuestions);
        Assert.Equal(1, composition.ByQuestionType["temporal-reasoning"].AbstentionQuestions);
    }

    [Fact]
    public async Task Composition_DenominatorsCannotDisagreeWithTheResult()
    {
        using var dataset = MixedDataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path
        });

        // Both are derived from QuestionResults, so a consumer recomputing either from the same list
        // gets the same numbers AgentEval reported.
        Assert.Equal(result.QuestionResults.Count, result.Composition!.TotalQuestions);
        Assert.Equal(
            result.QuestionResults.Count(q => q.IsAbstention),
            result.Composition.AbstentionQuestions);
        foreach (var (type, perType) in result.PerTypeResults)
            Assert.Equal(perType.TotalQuestions, result.Composition.ByQuestionType[type].TotalQuestions);
    }

    [Fact]
    public async Task Composition_EchoesTheRequestAlongsideTheOutcome()
    {
        using var dataset = MixedDataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            IncludeQuestionTypes = ["temporal-reasoning"],
            AbstentionPolicy = AbstentionSamplingPolicy.Exclude
        });

        Assert.Equal(["temporal-reasoning"], result.Composition!.RequestedQuestionTypes);
        Assert.Equal(AbstentionSamplingPolicy.Exclude, result.Composition.RequestedAbstentionPolicy);
        Assert.Null(result.Composition.RequestedAbstentionProportion);

        // And the realised counts show the request was honoured.
        Assert.Equal(2, result.Composition.TotalQuestions);
        Assert.Equal(0, result.Composition.AbstentionQuestions);
        Assert.All(result.QuestionResults, q => Assert.Equal("temporal-reasoning", q.QuestionType));
    }

    [Fact]
    public async Task Composition_AbstentionOnlyRun_ReportsEveryQuestionAsAbstention()
    {
        using var dataset = MixedDataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            AbstentionPolicy = AbstentionSamplingPolicy.Only
        });

        Assert.Equal(3, result.Composition!.TotalQuestions);
        Assert.Equal(3, result.Composition.AbstentionQuestions);
        Assert.Equal(1.0, result.Composition.RealisedAbstentionProportion);
        Assert.All(result.QuestionResults, q => Assert.True(q.IsAbstention));
    }

    // ── Ask 3 proof: retry attribution survives to the result ─────────────────

    [Fact]
    public async Task RetryCounts_ReachTheQuestionResultAndTheRunTotal()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        // Every question: one unreadable verdict, then a clean one.
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("I cannot tell", "yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            MaxJudgeRetries = 1
        });

        var question = Assert.Single(result.QuestionResults);
        Assert.Equal(2, question.JudgeLlmCallCount);
        Assert.Equal(1, question.JudgePrimaryLlmCallCount);
        Assert.Equal(1, question.JudgeRetryLlmCallCount);
        Assert.Equal(2, question.JudgeAttemptsUsed);

        // TotalLlmCalls mixes agent and judge calls; the retry total lets a caller reconcile the
        // difference from an expected budget instead of rejecting the run.
        Assert.Equal(1, result.TotalJudgeRetryLlmCalls);
        Assert.Equal(3, result.TotalLlmCalls);
        Assert.Equal(
            result.TotalLlmCalls - result.TotalJudgeRetryLlmCalls,
            result.QuestionResults.Sum(q => q.AgentLlmCallCount + q.JudgePrimaryLlmCallCount));
    }

    [Fact]
    public async Task RetryCounts_CleanRunReportsZeroRetries()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path
        });

        Assert.Equal(0, result.TotalJudgeRetryLlmCalls);
        Assert.Equal(2, result.TotalLlmCalls);
    }

    // ── Ask 5 proof: provenance values match the run ──────────────────────────

    [Fact]
    public async Task Provenance_DefaultsToNull()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path
        });

        Assert.Null(result.Provenance);
    }

    [Fact]
    public async Task Provenance_Full_MatchesTheDatasetAndSelectionOfThisRun()
    {
        using var dataset = MixedDataset.Create();
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            RunProvenanceMode = RunProvenanceMode.Full
        });

        var provenance = result.Provenance;
        Assert.NotNull(provenance);
        Assert.Equal(dataset.Path, provenance!.DatasetPath);
        Assert.Equal(LongMemEvalProvenance.TryComputeFileSha256(dataset.Path), provenance.DatasetSha256);
        Assert.Equal(new FileInfo(dataset.Path).Length, provenance.DatasetSizeBytes);
        // Questions in the file, which is not the same number as the questions that ran.
        Assert.Equal(9, provenance.DatasetQuestionCount);
        Assert.Equal(
            LongMemEvalProvenance.ComputeSelectedIdFingerprint(result.QuestionResults.Select(q => q.QuestionId)),
            provenance.SelectedQuestionIdFingerprint);
        Assert.Equal(LongMemEvalProvenance.ComputeJudgePromptFingerprint(), provenance.JudgePromptFingerprint);
    }

    [Fact]
    public async Task Provenance_Full_DistinguishesSamplesThatDifferOnlyBySeed()
    {
        using var dataset = MixedDataset.Create();

        async Task<string?> FingerprintForSeed(int seed)
        {
            var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);
            var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
            {
                DatasetPath = dataset.Path,
                MaxQuestions = 4,
                RandomSeed = seed,
                RunProvenanceMode = RunProvenanceMode.Full
            });
            return result.Provenance!.SelectedQuestionIdFingerprint;
        }

        Assert.Equal(await FingerprintForSeed(1), await FingerprintForSeed(1));
        Assert.NotEqual(await FingerprintForSeed(1), await FingerprintForSeed(9));
    }

    [Fact]
    public async Task Provenance_PromptsOnly_SkipsTheDatasetHash()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            RunProvenanceMode = RunProvenanceMode.PromptsOnly
        });

        Assert.NotNull(result.Provenance!.JudgePromptFingerprint);
        Assert.Null(result.Provenance.DatasetSha256);
    }

    // ── Ask 6 proof: fingerprint reaches the stored run ───────────────────────

    [Fact]
    public async Task JudgeSystemFingerprint_IsCarriedOntoTheQuestionAndDeduplicatedOntoTheRun()
    {
        using var dataset = MixedDataset.Create(questionCount: 2);
        var runner = LongMemEvalBenchmarkRunner.Create(
            new FingerprintedJudgeClient("fp_build_A", "fp_build_B"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            RunProvenanceMode = RunProvenanceMode.PromptsOnly
        });

        Assert.Equal(["fp_build_A", "fp_build_B"], result.JudgeSystemFingerprints);
        // Two backend builds inside one run: its own questions were not answered under equal
        // conditions, and the report can now say so.
        Assert.Equal(2, result.JudgeSystemFingerprints!.Count);
        Assert.All(result.QuestionResults, q => Assert.NotNull(q.JudgeSystemFingerprint));
    }

    [Fact]
    public async Task JudgeSystemFingerprint_AbsentProviderValueLeavesItNull()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        var runner = LongMemEvalBenchmarkRunner.Create(Judge("yes"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path,
            RunProvenanceMode = RunProvenanceMode.PromptsOnly
        });

        Assert.Null(result.JudgeSystemFingerprints);
        Assert.Null(Assert.Single(result.QuestionResults).JudgeSystemFingerprint);
    }

    [Fact]
    public async Task SystemFingerprint_NotCollectedWhenProvenanceIsOff()
    {
        using var dataset = MixedDataset.Create(questionCount: 1);
        var runner = LongMemEvalBenchmarkRunner.Create(
            new FingerprintedJudgeClient("fp_build_A"), dataset.Path);

        var result = await runner.RunAsync(Agent(), Config(), new ExternalBenchmarkOptions
        {
            DatasetPath = dataset.Path
        });

        // The provider DID return a fingerprint; with provenance off it is deliberately not
        // collected, so a default run keeps its historical result shape and observations.
        Assert.Null(result.JudgeSystemFingerprints);
        Assert.Null(Assert.Single(result.QuestionResults).JudgeSystemFingerprint);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentBenchmarkConfig Config() => new() { AgentName = "test-agent" };

    private static IEvaluableAgent Agent() => new StubAgent();

    private static IChatClient Judge(params string[] cycle) => new CyclingJudgeClient(cycle);

    private sealed class StubAgent : IEvaluableAgent
    {
        public string Name => "stub";

        public Task<AgentResponse> InvokeAsync(string input, CancellationToken ct = default)
            => Task.FromResult(new AgentResponse { Text = "an answer" });

        public IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
            string input, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Replays a fixed cycle of judge texts, restarting for each question.</summary>
    private sealed class CyclingJudgeClient(string[] cycle) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var text = cycle[_index % cycle.Length];
            _index++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Answers "yes" while reporting a different backend build per call.</summary>
    private sealed class FingerprintedJudgeClient(params string[] fingerprints) : IChatClient
    {
        private int _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var fingerprint = fingerprints[_index % fingerprints.Length];
            _index++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["system_fingerprint"] = fingerprint
                }
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Nine questions across two types, three of them abstention — enough for per-type and
    /// per-abstention counts to be distinguishable from each other and from the total.
    /// </summary>
    private sealed class MixedDataset : IDisposable
    {
        public string Path { get; }

        private MixedDataset(string path) => Path = path;

        public static MixedDataset Create(int? questionCount = null)
        {
            var specs = new (string Id, string Type)[]
            {
                ("u-1", "single-session-user"),
                ("u-2", "single-session-user"),
                ("u-3", "single-session-user"),
                ("u-4", "single-session-user"),
                ("u-5_abs", "single-session-user"),
                ("u-6_abs", "single-session-user"),
                ("t-1", "temporal-reasoning"),
                ("t-2", "temporal-reasoning"),
                ("t-3_abs", "temporal-reasoning")
            };
            if (questionCount is { } take)
                specs = specs.Take(take).ToArray();

            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agenteval-lme-mixed-{Guid.NewGuid():N}.json");

            var questions = specs.Select(spec => new
            {
                question_id = spec.Id,
                question_type = spec.Type,
                question = $"Question {spec.Id}?",
                answer = "gold answer",
                question_date = "2026/01/02 (Fri) 00:00",
                haystack_sessions = new[]
                {
                    new object[]
                    {
                        new { role = "user", content = $"history {spec.Id}", has_answer = true },
                        new { role = "assistant", content = $"reply {spec.Id}", has_answer = false }
                    }
                },
                haystack_dates = new[] { "2026/01/01 (Thu) 00:00" },
                haystack_session_ids = new[] { $"session-{spec.Id}" },
                answer_session_ids = new[] { $"session-{spec.Id}" }
            });

            File.WriteAllText(path, JsonSerializer.Serialize(questions));
            return new MixedDataset(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
