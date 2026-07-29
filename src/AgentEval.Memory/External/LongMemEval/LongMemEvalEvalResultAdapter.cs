// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Projects a native LongMemEval result into AgentEval's recursive report shape.
/// </summary>
/// <remarks>
/// The projection is safe for default reports: it includes typed execution, judge, and
/// evidence-capture status but never copies question text, gold answers, agent responses,
/// judge explanations, or captured evidence content. Keep the native result as a separate,
/// access-controlled artefact when full diagnostic detail is required.
/// </remarks>
public static class LongMemEvalEvalResultAdapter
{
    /// <summary>
    /// Converts <paramref name="result"/> into an <see cref="EvalResult"/> tree containing
    /// per-type composites and per-question leaves.
    /// </summary>
    /// <param name="result">The native LongMemEval result.</param>
    /// <param name="presetName">The preset label displayed in reports.</param>
    /// <param name="judgeModel">The declared judge model identity, when known.</param>
    /// <param name="passThresholdPercent">Pass threshold on the native 0–100 scale.</param>
    public static EvalResult ToEvalResult(
        ExternalBenchmarkResult result,
        string presetName,
        string? judgeModel = null,
        double passThresholdPercent = 50.0)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(presetName);
        if (!double.IsFinite(passThresholdPercent) || passThresholdPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(passThresholdPercent));

        var now = DateTimeOffset.UtcNow;
        var passThreshold = passThresholdPercent / 100.0;
        var perTypeNodes = new List<EvalResult>();

        foreach (var (typeName, typeResult) in result.PerTypeResults.OrderBy(kv => kv.Key))
        {
            var typeQuestions = result.QuestionResults
                .Where(q => string.Equals(q.QuestionType, typeName, StringComparison.Ordinal))
                .ToList();
            var leaves = typeQuestions
                .Select(q => BuildQuestionLeaf(q, judgeModel, now))
                .ToList();

            var typeAccuracy = typeResult.Accuracy;
            var typeAccuracy01 = typeAccuracy.GetValueOrDefault() / 100.0;
            var (typeLabel, typePassed, typeSeverity) =
                Classify(typeAccuracy, typeAccuracy01, passThreshold);

            perTypeNodes.Add(new EvalResult(
                Metric: new EvalMetadata(
                    Key: $"longmemeval.{typeName}",
                    Name: $"LongMemEval — {typeName}",
                    Category: "memory",
                    Version: "1.0.0"),
                Score: new EvalScore(
                    Value: typeAccuracy01,
                    Ordinal: null,
                    Label: typeLabel,
                    Passed: typePassed,
                    Threshold: passThreshold,
                    Severity: typeSeverity,
                    Confidence: null),
                Details: new EvalDetails(
                    Dimensions: new Dictionary<string, double>
                    {
                        ["selectedQuestions"] = typeResult.TotalQuestions,
                        ["scoredQuestions"] = typeResult.ScoredQuestions,
                        ["correctQuestions"] = typeResult.CorrectQuestions,
                        ["incorrectQuestions"] =
                            Math.Max(0, typeResult.ScoredQuestions - typeResult.CorrectQuestions),
                        ["inconclusiveQuestions"] = typeResult.InconclusiveQuestions,
                        ["agentFailureQuestions"] = typeResult.AgentFailureQuestions,
                    },
                    Evidence: null,
                    Recommendations: null,
                    SubResults: leaves,
                    AggregationStrategy: "scored-questions-only"),
                Provenance: new EvalProvenance(
                    Type: "composite",
                    JudgeModel: judgeModel,
                    PromptId: null,
                    PromptHash: null,
                    TokensUsed: null,
                    EstimatedCost: 0,
                    CacheHit: false),
                EvaluatedAt: now));
        }

        var overallAccuracy = result.OverallAccuracy;
        var overall01 = overallAccuracy.GetValueOrDefault() / 100.0;
        var (overallLabel, overallPassed, overallSeverity) =
            Classify(overallAccuracy, overall01, passThreshold);

        var rootDimensions = new Dictionary<string, double>
        {
            ["selectedQuestions"] = result.SelectedQuestions,
            ["agentCompletedQuestions"] = result.AgentCompletedQuestions,
            ["scoredQuestions"] = result.ScoredQuestions,
            ["correctQuestions"] = result.CorrectQuestions,
            ["incorrectQuestions"] = result.IncorrectQuestions,
            ["inconclusiveQuestions"] = result.InconclusiveQuestions,
            ["agentFailureQuestions"] = result.AgentFailureQuestions,
            ["scoredTypeCount"] = result.ScoredTypeCount,
            ["totalLlmCalls"] = result.TotalLlmCalls,
            ["durationSeconds"] = result.Duration.TotalSeconds,
            ["judgeFailurePolicy"] = (int)result.Options.JudgeFailurePolicy,
            ["maxJudgeRetries"] = result.Options.MaxJudgeRetries,
            ["judgeTemperatureConfigured"] = result.Options.JudgeTemperature.HasValue ? 1 : 0,
            ["judgeMaxOutputTokens"] = result.Options.JudgeMaxOutputTokens,
            ["judgeEvidenceMode"] = (int)result.Options.JudgeEvidenceMode,
            ["evidenceCaptureMode"] = (int)result.Options.EvidenceCaptureMode,
            ["evidenceTopK"] = result.Options.EvidenceTopK,
        };
        if (result.Options.JudgeTemperature is { } judgeTemperature)
            rootDimensions["judgeTemperature"] = judgeTemperature;
        if (overallAccuracy is { } overallAccuracyPercent)
            rootDimensions["overallAccuracyPercent"] = overallAccuracyPercent;
        if (result.TaskAveragedAccuracy is { } taskAveragedAccuracyPercent)
            rootDimensions["taskAveragedAccuracyPercent"] = taskAveragedAccuracyPercent;
        if (result.JudgeFailureRate is { } judgeFailureRate)
            rootDimensions["judgeFailureRate"] = judgeFailureRate;

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: "longmemeval",
                Name: $"LongMemEval — {presetName} preset (ICLR 2025)",
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: overall01,
                Ordinal: null,
                Label: overallLabel,
                Passed: overallPassed,
                Threshold: passThreshold,
                Severity: overallSeverity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: rootDimensions,
                Evidence: null,
                Recommendations:
                [
                    "Compare against a declared dataset mode, answer model, and judge model. " +
                    "Small samples are high variance and inconclusive questions are excluded " +
                    "from the default accuracy denominator."
                ],
                SubResults: perTypeNodes,
                AggregationStrategy: "scored-questions-only"),
            Provenance: new EvalProvenance(
                Type: "composite",
                JudgeModel: judgeModel,
                PromptId: null,
                PromptHash: null,
                TokensUsed: result.QuestionResults.Sum(q => q.JudgeTokensUsed),
                EstimatedCost: result.EstimatedCostUsd ?? 0,
                CacheHit: false),
            EvaluatedAt: now);
    }

    private static EvalResult BuildQuestionLeaf(
        QuestionResult question,
        string? judgeModel,
        DateTimeOffset evaluatedAt)
    {
        var label = question.Correct switch
        {
            true => "pass",
            false => "fail",
            null => "inconclusive",
        };
        var dimensions = new Dictionary<string, double>
        {
            ["isScored"] = question.Correct.HasValue ? 1 : 0,
            ["isInconclusive"] = question.Correct.HasValue ? 0 : 1,
            ["agentCompleted"] =
                question.ExecutionStatus == QuestionExecutionStatus.Completed ? 1 : 0,
            ["agentLlmCalls"] = question.AgentLlmCallCount,
            ["judgeLlmCalls"] = question.JudgeLlmCallCount,
            ["judgeTokensUsed"] = question.JudgeTokensUsed,
            ["hasCapturedEvidence"] = question.Evidence is null ? 0 : 1,
            ["hasEvidenceDiagnostics"] = question.EvidenceDiagnostics is null ? 0 : 1,
        };
        if (question.RawScore is { } rawScore && double.IsFinite(rawScore))
            dimensions["judgeRawScore"] = rawScore;

        var statusEvidence = new List<EvalEvidence>
        {
            new(
                Source: "execution-status",
                Reference: question.QuestionId,
                Message: question.ExecutionStatus.ToString()),
            new(
                Source: "judge-status",
                Reference: question.QuestionId,
                Message: question.JudgeStatus?.ToString() ?? "NotRun"),
            new(
                Source: "evidence-status",
                Reference: question.QuestionId,
                Message: question.Evidence is null
                    ? "NotCaptured"
                    : question.EvidenceDiagnostics is null ? "Captured" : "CapturedAndDiagnosed"),
        };
        var safeFailureCode = question.SafeFailureCode;
        if (IsSafeFailureCode(safeFailureCode))
        {
            statusEvidence.Add(new(
                Source: "safe-failure-code",
                Reference: question.QuestionId,
                Message: safeFailureCode!));
        }

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: $"longmemeval.{question.QuestionType}.{question.QuestionId}",
                Name: $"{question.QuestionType} — {question.QuestionId}",
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: question.Correct is true ? 1 : 0,
                Ordinal: null,
                Label: label,
                Passed: question.Correct is true,
                Threshold: 1,
                Severity: question.Correct switch
                {
                    true => "none",
                    false => "medium",
                    null => "low",
                },
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: dimensions,
                Evidence: statusEvidence,
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new EvalProvenance(
                Type: "external-benchmark",
                JudgeModel: judgeModel,
                PromptId: null,
                PromptHash: null,
                TokensUsed: question.JudgeTokensUsed,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: evaluatedAt);
    }

    private static (string Label, bool Passed, string Severity) Classify(
        double? accuracyPercent,
        double accuracy01,
        double passThreshold)
    {
        if (!accuracyPercent.HasValue)
            return ("inconclusive", false, "low");
        if (!double.IsFinite(accuracyPercent.Value) || accuracyPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(accuracyPercent));
        if (accuracy01 < passThreshold)
            return ("fail", false, "medium");
        return accuracy01 >= 0.7
            ? ("pass", true, "none")
            : ("warn", true, "low");
    }

    private static bool IsSafeFailureCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
