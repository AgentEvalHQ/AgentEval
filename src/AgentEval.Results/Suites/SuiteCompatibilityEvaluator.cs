// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;

namespace AgentEval.Results.Suites;

/// <summary>Strict suite identity compatibility, independent of score eligibility or statistical significance.</summary>
public sealed record SuiteCompatibility(bool Compatible, string Reason, string Description, SuiteIdentity Baseline, SuiteIdentity Candidate)
{
    /// <summary>Versioned policy used by this comparison; distinct from AEF checkpoint lane rules.</summary>
    public const string Policy = "agenteval/full-definition-v1";

    /// <summary>Detached machine-readable report, including the exact selected seal hashes.</summary>
    public JsonObject ToJson() => new()
    {
        ["policy"] = Policy, ["compatible"] = Compatible, ["reason"] = Reason, ["description"] = Description,
        ["baseline"] = Baseline.ToJson(), ["candidate"] = Candidate.ToJson(),
    };
}

/// <summary>Compares verified suite identities without filesystem access or outcome-dependent decisions.</summary>
public interface ISuiteCompatibilityEvaluator
{
    /// <summary>Requires two matching full frozen definitions; recipes and legacy digests never prove equivalence.</summary>
    SuiteCompatibility Compare(SuiteIdentity baseline, SuiteIdentity candidate);
}

/// <summary>AgentEval's strict full-definition policy, layered above the unchanged AEF lane protocol.</summary>
public sealed class SuiteCompatibilityEvaluator : ISuiteCompatibilityEvaluator
{
    /// <inheritdoc />
    public SuiteCompatibility Compare(SuiteIdentity baseline, SuiteIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.Scope == "recipe" || candidate.Scope == "recipe")
            return Result(false, "generated-content", "A generation recipe does not identify fixed evaluation content.");
        if (baseline.Scope != "definition" || candidate.Scope != "definition")
            return Result(false, "definition-required", "Both runs must declare full frozen definitions; missing, dataset-only and roster-only identities are insufficient.");
        if (baseline.Definition!.Digest != candidate.Definition!.Digest)
            return Result(false, "definition-mismatch", "The frozen definitions differ; inspect their declared inputs, configuration and planned rosters.");
        return Result(true, "same-definition", "Both sealed runs bind the same frozen suite definition. Check run completeness, subject and execution conditions separately before comparing scores.");

        SuiteCompatibility Result(bool compatible, string reason, string description) => new(compatible, reason, description, baseline, candidate);
    }
}
