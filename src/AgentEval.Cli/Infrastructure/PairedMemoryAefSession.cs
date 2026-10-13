// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

internal sealed record PairedMemoryArtifact(LongMemEvalArm Arm, string Directory, string? RunHash, string Status);
internal sealed class PairedMemoryEvidenceException(string message, Exception inner) : IOException(message, inner);

/// <summary>Captures and writes each prepared arm once. Oracle links backward to normal; no cyclic seal references.</summary>
internal sealed class PairedMemoryAefSession(string runsRoot, string preset, AefCompositeRunOptions options, string? judgeProvider)
{
    private readonly List<PairedMemoryArtifact> _artifacts = [];
    private LongMemEvalPairedPlan? _plan;
    private ArmState? _active;
    private ExternalBenchmarkResult? _normal;
    private bool _writeFailed;
    private bool _started;

    internal IReadOnlyList<PairedMemoryArtifact> Artifacts => _artifacts.AsReadOnly();
    internal LongMemEvalPairedPlan? Plan => _plan;

    internal async Task<LongMemEvalPairedResult> RunAsync(LongMemEvalBenchmarkRunner runner, IEvaluableAgent normalAgent,
        AgentBenchmarkConfig normalConfig, IChatClient oracleClient, AgentBenchmarkConfig oracleConfig,
        ExternalBenchmarkOptions benchmarkOptions, CancellationToken ct,
        Action<LongMemEvalArm, QuestionResult>? onQuestion = null, Action<PairedMemoryArtifact>? onSaved = null)
    {
        if (_started) throw new InvalidOperationException("Expected: one execution per paired evidence session. Actual: session already started. Suggestions: create a new session. Because: paired identities cannot be reused for another execution.");
        _started = true;
        try { Directory.CreateDirectory(runsRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw EvidenceError(ex); }
        try
        {
            return await runner.RunPairedWithProgressAsync(normalAgent, normalConfig, oracleClient, oracleConfig, benchmarkOptions,
                new LongMemEvalPairedProgress
                {
                    OnPrepared = plan => _plan = plan,
                    OnArmPrepared = (arm, plan) => _active = new(arm,
                        PairedMemorySuiteDefinition.Create(preset, _plan!, arm, plan, judgeProvider), DateTimeOffset.UtcNow),
                    OnQuestionCompleted = (arm, question) =>
                    {
                        _active!.Questions.Add(question);
                        onQuestion?.Invoke(arm, question);
                    },
                    OnArmCompleted = (arm, result) =>
                    {
                        _active!.Result = result;
                        Write(_active, null);
                        if (arm == LongMemEvalArm.Normal) _normal = result;
                        onSaved?.Invoke(_artifacts[^1]);
                    },
                }, ct);
        }
        catch
        {
            if (!_writeFailed && _active is { Written: false } active)
            {
                Write(active, ct.IsCancellationRequested ? "cancelled" : "execution-error");
                onSaved?.Invoke(_artifacts[^1]);
            }
            throw;
        }
    }

    private void Write(ArmState state, string? abortReason)
    {
        try
        {
            var role = state.Arm == LongMemEvalArm.Normal ? "normal" : "oracle";
            var result = state.Result ?? new ExternalBenchmarkResult
            {
                BenchmarkId = "longmemeval", BenchmarkName = "Interrupted paired LongMemEval",
                Options = new(), OverallAccuracy = null, TaskAveragedAccuracy = null, PerTypeResults = [],
                QuestionResults = state.Questions.ToArray(), Duration = DateTimeOffset.UtcNow - state.StartedAt,
            };
            var verdict = abortReason is not null || result.OverallAccuracy is null ? "INCONCLUSIVE"
                : state.Arm == LongMemEvalArm.Oracle ? "SCORED"
                : BenchLongMemEvalCommand.LongMemEvalVerdict(result.OverallAccuracy, result.QuestionResults.Count - result.ScoredQuestions);
            // Both arms use LongMemEval's LLM judge even when a client exposes no model metadata.
            var tree = BenchAef.QuestionTree(result, "longmemeval", preset + "/" + role, verdict, _plan!.JudgeModelId ?? "unreported");
            var normalArtifact = _artifacts.FirstOrDefault(a => a.Arm == LongMemEvalArm.Normal);
            var pair = new JsonObject
            {
                ["schemaVersion"] = "1.0", ["pairId"] = _plan.PairId, ["role"] = role,
                ["sourceInputsDigest"] = _plan.Source.SelectedInputsDigest,
                ["plannedQuestions"] = _plan.Source.QuestionIds.Count,
                ["observedQuestions"] = result.QuestionResults.Count,
                ["scoredQuestions"] = state.Result?.ScoredQuestions,
                ["totalLlmCalls"] = state.Result?.TotalLlmCalls,
                ["complete"] = abortReason is null, ["diagnosticOnly"] = state.Arm == LongMemEvalArm.Oracle,
                ["accuracyPercent"] = abortReason is null ? result.OverallAccuracy : null,
            };
            if (state.Arm == LongMemEvalArm.Oracle)
            {
                pair["normalRunHash"] = normalArtifact?.RunHash;
                pair["oracleGapPercentagePoints"] = abortReason is null && _normal?.OverallAccuracy is { } normalAccuracy
                    && result.OverallAccuracy is { } oracleAccuracy ? oracleAccuracy - normalAccuracy : null;
            }
            var writerOptions = options with
            {
                SuiteRef = state.Definition.SuiteRef, Definition = state.Definition, Lane = "longmemeval",
                AbortReason = abortReason,
                // Invocation-wide usage must not be copied into each arm and counted twice.
                Usage = null, StoreV1 = null,
                Gate = state.Arm == LongMemEvalArm.Normal && abortReason is null
                    ? BenchAef.Gate(verdict, BenchLongMemEvalCommand.VerdictExit(verdict)) : null,
                SummaryExt = new JsonObject { ["agenteval.memoryPair"] = pair },
            };
            var (directory, hash) = abortReason is not null && state.Questions.Count == 0
                ? AefCompositeRunWriter.WriteAbortedEmpty(runsRoot, tree, writerOptions, state.StartedAt, DateTimeOffset.UtcNow)
                : AefCompositeRunWriter.Write(runsRoot, tree, AefCompositeRunWriter.Undescribed, writerOptions, state.StartedAt, DateTimeOffset.UtcNow);
            state.Written = true;
            _artifacts.Add(new(state.Arm, directory, hash, abortReason is null ? "completed" : "aborted"));
        }
        catch (Exception ex)
        {
            _writeFailed = true;
            throw EvidenceError(ex);
        }
    }

    private static PairedMemoryEvidenceException EvidenceError(Exception ex) => new(
        $"Expected: paired AEF evidence could be written. Actual: {ex.Message}. Suggestions: check the evidence destination and signing configuration. Because: an arm must not claim evidence that was not retained.", ex);

    private sealed class ArmState(LongMemEvalArm arm, SuiteDefinitionManifest definition, DateTimeOffset startedAt)
    {
        internal LongMemEvalArm Arm { get; } = arm;
        internal SuiteDefinitionManifest Definition { get; } = definition;
        internal DateTimeOffset StartedAt { get; } = startedAt;
        internal List<QuestionResult> Questions { get; } = [];
        internal ExternalBenchmarkResult? Result { get; set; }
        internal bool Written { get; set; }
    }
}
