// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Articles.Models;

namespace AgentEval.Compliance.Gdpr.Articles.Validation;

/// <summary>Validates a loaded <see cref="ArticleSpec"/> against the GDPR benchmark schema rules.</summary>
public sealed class ArticleYamlValidator
{
    private static readonly string[] s_validPillars =
        ["Pillar1-Foundations", "Pillar2-LawfulBasis", "Pillar3-SubjectRights", "Pillar4-Transparency", "Pillar5-PrivacyDesign", "Pillar6-Governance"];

    private static readonly string[] s_validSeverities = ["low", "medium", "high", "critical"];

    private static readonly string[] s_validPatterns = ["direct", "trap", "edge-case", "probe"];

    private static readonly string[] s_validGranularities = ["atomic", "composite"];

    private static readonly string[] s_validAggregations = ["weighted_sum", "min", "cap_by_worst"];

    /// <summary>Validates <paramref name="spec"/> and returns a <see cref="ValidationResult"/>.</summary>
    public ValidationResult Validate(ArticleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var errors = new List<string>();

        // ── Metadata ─────────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(spec.Metadata.ControlId))
            errors.Add("metadata.control_id is required");

        if (!s_validPillars.Contains(spec.Metadata.Pillar))
            errors.Add($"metadata.pillar '{spec.Metadata.Pillar}' must be one of: {string.Join(", ", s_validPillars)}");

        if (!s_validSeverities.Contains(spec.Metadata.Severity))
            errors.Add($"metadata.severity '{spec.Metadata.Severity}' must be one of: {string.Join(", ", s_validSeverities)}");

        if (!s_validAggregations.Contains(spec.Metadata.Aggregation))
            errors.Add($"metadata.aggregation '{spec.Metadata.Aggregation}' must be one of: {string.Join(", ", s_validAggregations)}");

        // 16: <= 0, not < 0 — PassThreshold directly gates pass/fail (ArticleCompositeBuilder), unlike
        // WarnThreshold/PillarWeight (documented dead metadata below, not consumed). An omitted YAML field
        // silently deserializes to the C# default 0.0, and a 0.0 threshold trivially passes every score ≥ 0 —
        // silently making the whole article auto-pass with zero actual signal, exactly the failure mode this
        // benchmark exists to catch. Every real article ships 0.50-0.85; 0.0 is never intentional.
        if (spec.Metadata.PassThreshold is <= 0 or > 1)
            errors.Add("metadata.pass_threshold must be in (0,1]");

        if (spec.Metadata.WarnThreshold is < 0 or > 1)
            errors.Add("metadata.warn_threshold must be in [0,1]");

        if (spec.Metadata.PillarWeight is < 0 or > 1)
            errors.Add("metadata.pillar_weight must be in [0,1]");

        // ── Scenarios ─────────────────────────────────────────────────────────
        if (spec.Scenarios.Count == 0)
            errors.Add("article must have at least one scenario");

        var weightSum = spec.Scenarios.Sum(s => s.Weight);
        if (Math.Abs(weightSum - 1.0) > 0.001)
            errors.Add($"scenario weights must sum to 1.0; got {weightSum:F3}");

        foreach (var s in spec.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                errors.Add("scenario.id is required");

            if (!s_validPatterns.Contains(s.Pattern))
                errors.Add($"scenario {s.Id}.pattern '{s.Pattern}' must be one of: {string.Join(", ", s_validPatterns)}");

            if (!s_validGranularities.Contains(s.Granularity))
                errors.Add($"scenario {s.Id}.granularity '{s.Granularity}' must be one of: {string.Join(", ", s_validGranularities)}");

            if (s.Weight is < 0 or > 1)
                errors.Add($"scenario {s.Id}.weight must be in [0,1]");

            if (s.EvaluationCriteria.Count == 0)
                errors.Add($"scenario {s.Id} must have at least one evaluation_criteria");
        }

        // Phase-6 Task 6.10: duplicate scenario IDs silently shadowed each other
        // during composite renormalisation, so a copy-pasted scenario could
        // double-count its weight without being flagged.
        var dupIds = spec.Scenarios
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var id in dupIds)
            errors.Add($"scenario id '{id}' is duplicated within article '{spec.Metadata.ControlId}'");

        return new ValidationResult(errors.Count == 0, errors);
    }
}

/// <summary>Represents the outcome of a YAML validation pass.</summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    /// <summary>All validation errors joined by '; '.</summary>
    public string ErrorsJoined => string.Join("; ", Errors);
}
