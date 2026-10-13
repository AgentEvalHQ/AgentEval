// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;
using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>The separately evaluated paths of a paired LongMemEval execution.</summary>
public enum LongMemEvalArm
{
    /// <summary>The normal memory agent, completed before constructing the oracle path.</summary>
    Normal,
    /// <summary>The gold-only, retrieval-bypassing oracle reader.</summary>
    Oracle,
}

/// <summary>Immutable, content-free declaration of one paired execution over a single selected source workload.</summary>
public sealed class LongMemEvalPairedPlan
{
    [JsonConstructor]
    internal LongMemEvalPairedPlan(string pairId, ExternalBenchmarkPlan source, LongMemEvalModelIdentity normalAnswerModel,
        LongMemEvalModelIdentity oracleAnswerModel, string? judgeModelId)
    {
        PairId = pairId;
        Source = source;
        NormalAnswerModel = normalAnswerModel;
        OracleAnswerModel = oracleAnswerModel;
        JudgeModelId = judgeModelId;
    }

    internal LongMemEvalPairedPlan(ExternalBenchmarkPlan source, AgentBenchmarkConfig normal, AgentBenchmarkConfig oracle, string? judgeModel)
    {
        PairId = "pair-" + Guid.NewGuid().ToString("N");
        Source = source;
        NormalAnswerModel = LongMemEvalModelIdentity.From(normal);
        OracleAnswerModel = LongMemEvalModelIdentity.From(oracle);
        JudgeModelId = judgeModel;
    }

    /// <summary>Unique execution association; not a suite content digest and not stable across repeated runs.</summary>
    public string PairId { get; }
    /// <summary>Actual selected source entries and options, captured once before either arm executes.</summary>
    public ExternalBenchmarkPlan Source { get; }
    /// <summary>Declared normal answer-model settings; does not prove an opaque agent applies them.</summary>
    public LongMemEvalModelIdentity NormalAnswerModel { get; }
    /// <summary>Matching declared oracle answer-model settings, separate from client-reported metadata.</summary>
    public LongMemEvalModelIdentity OracleAnswerModel { get; }
    /// <summary>Judge's reported model identifier when available before execution.</summary>
    public string? JudgeModelId { get; }
}

/// <summary>
/// Synchronous observation hooks for paired evidence capture. Exceptions propagate and stop execution;
/// cancellation or failure before normal completion never starts the oracle path.
/// </summary>
public sealed class LongMemEvalPairedProgress
{
    /// <summary>Called once with the selected source workload, before either arm starts.</summary>
    public Action<LongMemEvalPairedPlan>? OnPrepared { get; init; }
    /// <summary>Called before each arm's provider calls; oracle inputs are materialized only after normal completion.</summary>
    public Action<LongMemEvalArm, ExternalBenchmarkPlan>? OnArmPrepared { get; init; }
    /// <summary>Called after each observed question, allowing partial evidence retention on interruption.</summary>
    public Action<LongMemEvalArm, QuestionResult>? OnQuestionCompleted { get; init; }
    /// <summary>Called after each arm finishes, before advancing to the next arm or returning the paired result.</summary>
    public Action<LongMemEvalArm, ExternalBenchmarkResult>? OnArmCompleted { get; init; }
}
