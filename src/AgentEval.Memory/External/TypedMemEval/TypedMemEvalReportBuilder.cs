// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Assembles a finished TypedMemEval run into an <see cref="ExternalBenchmarkResult"/>.
/// </summary>
/// <remarks>
/// Builds the family's own provenance rather than reusing the LongMemEval capture path. That path
/// stamps the LongMemEval judge-prompt fingerprint and template names, which on a family run would
/// be a false statement about which prompts produced the verdicts — and the fingerprint is the one
/// field whose entire job is to make judge drift visible.
/// </remarks>
internal static class TypedMemEvalReportBuilder
{
    private static readonly string[] TemplateNames =
        ["Standard", "Prospective", "Arithmetic", "ListOrder", "Forgetting"];

    internal static ExternalBenchmarkResult Build(
        TypedMemEvalVerticalDescriptor descriptor,
        ExternalBenchmarkOptions options,
        TypedMemEvalOptions facade,
        IReadOnlyList<QuestionResult> results,
        IReadOnlyDictionary<string, TypedMemEvalQuestionDetail> details,
        IReadOnlyDictionary<string, string> unrunReasons,
        TimeSpan duration,
        string? executionLabel,
        OracleProjectionReport? oracleProjection,
        int totalQuestionsInCorpus,
        string corpusSha256,
        double? calibratedFloorMean)
    {
        var typed = BuildTypedReport(
            descriptor, results, details, unrunReasons, corpusSha256, calibratedFloorMean);

        var perType = results
            .GroupBy(q => q.QuestionType)
            .ToDictionary(
                g => g.Key,
                g => new TypeResult
                {
                    TypeName = g.Key,
                    TotalQuestions = g.Count(),
                    CorrectQuestions = g.Count(q => q.Correct is true),
                    ScoredQuestions = g.Count(q => q.Correct.HasValue),
                    InconclusiveQuestions = g.Count(q =>
                        q.ExecutionStatus == QuestionExecutionStatus.Completed && !q.Correct.HasValue),
                    AgentFailureQuestions = g.Count(q =>
                        q.ExecutionStatus == QuestionExecutionStatus.AgentError),
                    Duration = TimeSpan.FromTicks(g.Sum(q => q.Duration.Ticks))
                });

        var correct = results.Count(q => q.Correct is true);
        var incorrect = results.Count(q => q.Correct is false);
        var scored = results.Count(q => q.Correct.HasValue);
        var completed = results.Count(q => q.ExecutionStatus == QuestionExecutionStatus.Completed);
        var inconclusive = results.Count(q =>
            q.ExecutionStatus == QuestionExecutionStatus.Completed && !q.Correct.HasValue);

        var typeAccuracies = perType.Values
            .Select(t => t.Accuracy)
            .Where(a => a.HasValue)
            .Select(a => a!.Value)
            .ToList();

        var name = $"{descriptor.DisplayName} {descriptor.Revision} {results.Count}q";
        if (!string.IsNullOrWhiteSpace(executionLabel))
            name += $" ({executionLabel})";

        return new ExternalBenchmarkResult
        {
            BenchmarkId = descriptor.BenchmarkId,
            BenchmarkName = name,
            // Populated for tooling compatibility only. The citable form of this result is
            // TypedOutcomes; see TypedMemEvalReport for the citation rule.
            OverallAccuracy = scored > 0 ? (double)correct / scored * 100 : null,
            TaskAveragedAccuracy = typeAccuracies.Count > 0 ? typeAccuracies.Average() : null,
            PerTypeResults = perType,
            QuestionResults = results,
            SelectedQuestions = results.Count,
            AgentCompletedQuestions = completed,
            ScoredQuestions = scored,
            CorrectQuestions = correct,
            IncorrectQuestions = incorrect,
            InconclusiveQuestions = inconclusive,
            AgentFailureQuestions = results.Count - completed,
            JudgeFailureRate = completed > 0 ? (double)inconclusive / completed : null,
            ScoredTypeCount = typeAccuracies.Count,
            Duration = duration,
            TotalLlmCalls = results.Sum(q => q.AgentLlmCallCount + q.JudgeLlmCallCount),
            TotalJudgeRetryLlmCalls = results.Sum(q => q.JudgeRetryLlmCallCount),
            Composition = new SampleComposition
            {
                TotalQuestions = results.Count,
                AbstentionQuestions = results.Count(q => q.IsAbstention),
                NonAbstentionQuestions = results.Count(q => !q.IsAbstention),
                ByQuestionType = results
                    .GroupBy(q => q.QuestionType)
                    .ToDictionary(
                        g => g.Key,
                        g => new QuestionTypeComposition
                        {
                            TotalQuestions = g.Count(),
                            AbstentionQuestions = g.Count(q => q.IsAbstention)
                        }),
                RequestedAbstentionPolicy = options.AbstentionPolicy
            },
            AnswerSampling = AnswerSamplingReport.From(
                options, results.Select(q => q.AnswerSampling).ToList()),
            OracleProjection = oracleProjection,
            TypedOutcomes = typed,
            Provenance = BuildProvenance(descriptor, results, totalQuestionsInCorpus, corpusSha256),
            JudgeSystemFingerprints = results
                .Select(q => q.JudgeSystemFingerprint)
                .Where(f => !string.IsNullOrEmpty(f))
                .Select(f => f!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray() is { Length: > 0 } fingerprints
                ? fingerprints
                : null,
            Options = options
        };
    }

    private static TypedMemEvalReport BuildTypedReport(
        TypedMemEvalVerticalDescriptor descriptor,
        IReadOnlyList<QuestionResult> results,
        IReadOnlyDictionary<string, TypedMemEvalQuestionDetail> details,
        IReadOnlyDictionary<string, string> unrunReasons,
        string corpusSha256,
        double? calibratedFloorMean)
    {
        var ordered = results
            .Select(r => details.TryGetValue(r.QuestionId, out var d) ? d : null)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToArray();

        var byShape = ordered
            .GroupBy(d => d.Shape, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => TypedMemEvalOutcomeCounts.From(g.Select(d => d.Outcome)),
                StringComparer.Ordinal);

        // A distance ladder averaged into one number would destroy the only thing the vertical
        // measures, so it is reported as a curve keyed by rung.
        var byDistance = descriptor.Vertical == TypedMemEvalVertical.WorkingMemory
            ? ordered
                .Where(d => d.DistanceSessions.HasValue)
                .GroupBy(d => d.DistanceSessions!.Value)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => TypedMemEvalOutcomeCounts.From(g.Select(d => d.Outcome)))
            : null;

        var observed = ordered.Where(d => d.RealisedGoldCoverage.HasValue).ToArray();

        return new TypedMemEvalReport
        {
            Vertical = descriptor.Slug,
            CorpusId = descriptor.CorpusId,
            Revision = descriptor.Revision,
            CorpusSha256 = corpusSha256,
            ReferenceBudgetSessions = TypedMemEvalCorpus.ReferenceBudgetSessions,
            Outcomes = TypedMemEvalOutcomeCounts.From(ordered.Select(d => d.Outcome)),
            ByShape = byShape,
            ByDistance = byDistance,
            PairConsistency = descriptor.Vertical == TypedMemEvalVertical.Prospective
                ? BuildPairConsistency(ordered)
                : null,
            StaleRecall = descriptor.Vertical == TypedMemEvalVertical.Forgetting
                ? ordered.Count(d => d.IsStaleRecall)
                : null,
            OverForgetting = descriptor.Vertical == TypedMemEvalVertical.Forgetting
                ? ordered.Count(d => d.IsOverForgetting)
                : null,
            MisattributedForgetting = descriptor.Vertical == TypedMemEvalVertical.Forgetting
                ? ordered.Count(d => d.IsMisattributedForgetting)
                : null,
            Attribution = new TypedMemEvalAttributionSummary
            {
                EvidencePresent = ordered.Count(d =>
                    d.Attribution == TypedMemEvalEvidenceAttribution.EvidencePresent),
                EvidenceAbsent = ordered.Count(d =>
                    d.Attribution == TypedMemEvalEvidenceAttribution.EvidenceAbsent),
                Unobserved = ordered.Count(d =>
                    d.Attribution == TypedMemEvalEvidenceAttribution.Unobserved),
                // Reported rather than implied: a run against an agent that supplies no evidence
                // telemetry must not look like a run where nothing was retrieved.
                ObservedShare = ordered.Length > 0
                    ? (double)ordered.Count(d =>
                        d.Attribution != TypedMemEvalEvidenceAttribution.Unobserved) / ordered.Length
                    : 0,
                Level = ordered.Length > 0
                    ? ordered.Max(d => d.AttributionLevel)
                    : TypedMemEvalAttributionLevel.None
            },
            Coverage = new TypedMemEvalCoverageSummary
            {
                Observed = observed.Length,
                Mean = observed.Length > 0 ? observed.Average(d => d.RealisedGoldCoverage!.Value) : null,
                Minimum = observed.Length > 0 ? observed.Min(d => d.RealisedGoldCoverage!.Value) : null,
                Maximum = observed.Length > 0 ? observed.Max(d => d.RealisedGoldCoverage!.Value) : null,
                CalibratedFloorMean = calibratedFloorMean
            },
            UnrunReasons = unrunReasons,
            JudgePromptFingerprint = TypedMemEvalJudge.PromptFingerprint
        };
    }

    /// <summary>
    /// Pair-level consistency. Only pairs with both arms present in the run are counted — a
    /// truncated sample can split a pair, and scoring half a pair as a pair would invent a finding.
    /// </summary>
    private static TypedMemEvalPairConsistency BuildPairConsistency(
        IReadOnlyList<TypedMemEvalQuestionDetail> details)
    {
        var pairs = details
            .Where(d => !string.IsNullOrEmpty(d.PairId))
            .GroupBy(d => d.PairId!, StringComparer.Ordinal)
            .Where(g => g.Count() == 2)
            .ToArray();

        var bothCorrect = 0;
        var premature = 0;
        var missedAfter = 0;
        var bothSame = 0;

        foreach (var pair in pairs)
        {
            var before = pair.FirstOrDefault(d => d.Arm == "before");
            var after = pair.FirstOrDefault(d => d.Arm == "after");
            if (before is null || after is null)
                continue;

            if (before.Outcome == TypedMemEvalOutcome.Correct && after.Outcome == TypedMemEvalOutcome.Correct)
                bothCorrect++;
            if (before.Outcome == TypedMemEvalOutcome.Premature)
                premature++;
            if (after.Outcome == TypedMemEvalOutcome.Missed)
                missedAfter++;
            // Gold flips between the arms by construction, so identical outcomes on both arms is
            // the fingerprint of a system that never received the query time — or ignored it. Named
            // so that finding reads as systematic rather than dissolving into random error.
            if (before.Outcome == after.Outcome)
                bothSame++;
        }

        return new TypedMemEvalPairConsistency
        {
            Pairs = pairs.Length,
            BothArmsCorrect = bothCorrect,
            PrematureBefore = premature,
            MissedAfter = missedAfter,
            BothArmsSameOutcome = bothSame
        };
    }

    private static BenchmarkRunProvenance BuildProvenance(
        TypedMemEvalVerticalDescriptor descriptor,
        IReadOnlyList<QuestionResult> results,
        int totalQuestionsInCorpus,
        string corpusSha256)
        => new()
        {
            Mode = RunProvenanceMode.Full,
            JudgePromptFingerprint = TypedMemEvalJudge.PromptFingerprint,
            JudgePromptTemplateNames = TemplateNames,
            AgentEvalVersion = LongMemEvalProvenance.TryGetAgentEvalVersion(),
            // No path by design: the family is embedded-only, and the identifier plus hash pin the
            // corpus more precisely than a path ever could.
            DatasetPath = null,
            DatasetIdentifier = descriptor.CorpusId,
            DatasetSha256 = corpusSha256,
            DatasetQuestionCount = totalQuestionsInCorpus,
            SelectedQuestionIdFingerprint = SelectedIdFingerprint(results.Select(r => r.QuestionId))
        };

    /// <summary>
    /// SHA-256 over the ordered selected question ids. Two runs sharing this drew exactly the same
    /// questions in the same order.
    /// </summary>
    private static string SelectedIdFingerprint(IEnumerable<string> questionIds)
    {
        var joined = string.Join("\u001f", questionIds);
        // Not a security function: this identifies which questions a run drew.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}
