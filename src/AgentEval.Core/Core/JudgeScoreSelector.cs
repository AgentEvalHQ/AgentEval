// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.Core;

/// <summary>Pure selection of a score from a judge verdict; never mutates the verdict or caller's criteria.</summary>
public static class JudgeScoreSelector
{
    /// <summary>Selects a score, requiring exactly one valid verdict per declared criterion for fraction scoring.</summary>
    public static JudgeScoreSelection Select(JudgeScorePolicy policy, EvaluationResult verdict,
        IReadOnlyList<string> criteria, IReadOnlyList<JudgeCriterion>? identifiedCriteria = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(criteria);
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        var selection = new JudgeScoreSelection
        {
            PolicyId = policy == JudgeScorePolicy.JudgeOverall ? JudgeScoreSelection.OverallPolicyId : JudgeScoreSelection.FractionPolicyId,
            RawOverallScore = verdict.OverallScore,
            ExpectedCount = identifiedCriteria?.Count ?? criteria.Count
        };
        if (verdict.EvaluationFailed) return selection with { FailureCode = "judge-failed" };
        if (policy == JudgeScorePolicy.JudgeOverall)
            return verdict.OverallScore is >= 0 and <= 100
                ? selection with { SelectedScore = verdict.OverallScore }
                : selection with { FailureCode = "overall-score-unavailable" };

        var expected = identifiedCriteria is null ? criteria.ToArray() : identifiedCriteria.Select(c => c.Id).ToArray();
        if (expected.Length == 0) return selection with { FailureCode = "no-criteria" };
        if (expected.Any(string.IsNullOrWhiteSpace) || expected.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            (identifiedCriteria is null && expected.Select(CriterionText.Normalize).Distinct(StringComparer.Ordinal).Count() != expected.Length))
            return selection with { FailureCode = "ambiguous-criteria" };
        if (!verdict.CriteriaResultsValid) return selection with { FailureCode = "invalid-verdict-set" };
        if (verdict.CriteriaResults.Any(c => c is null)) return selection with { FailureCode = "invalid-criterion-verdict" };

        var results = identifiedCriteria is null
            ? CriterionText.RealignToDeclared(verdict.CriteriaResults, criteria) : verdict.CriteriaResults;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        int applicable = 0, met = 0;
        foreach (var result in results)
        {
            var key = identifiedCriteria is null ? result.Criterion : result.Id;
            if (key is null || !allowed.Contains(key)) return selection with { FailureCode = "unknown-criterion" };
            if (!seen.Add(key)) return selection with { FailureCode = "duplicate-criterion" };
            if (!result.IsMeasured) return selection with { FailureCode = "unmeasured-criterion" };
            if (!result.Applicable) continue;
            applicable++;
            if (result.Met) met++;
        }
        if (seen.Count != expected.Length) return selection with { FailureCode = "missing-criterion" };
        return selection with
        {
            ApplicableCount = applicable, MetCount = met,
            SelectedScore = applicable == 0 ? null : 100.0 * met / applicable,
            FailureCode = applicable == 0 ? "no-applicable-criteria" : null
        };
    }

    /// <summary>Includes nondefault selection in a known instrument identity. An unknown prompt remains unknown.</summary>
    public static string? BindInstrument(string? rubricDigest, JudgeScorePolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (rubricDigest is null || policy == JudgeScorePolicy.JudgeOverall) return rubricDigest;
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Instrument = rubricDigest, ScorePolicy = JudgeScoreSelection.FractionPolicyId
        })))).ToLowerInvariant();
    }
}
