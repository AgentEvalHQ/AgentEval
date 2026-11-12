// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Quality;

/// <summary>Versioned meaning of the groundedness dimensions.</summary>
public enum GroundednessPolicy
{
    /// <summary>The original policy includes coverage of the available context.</summary>
    LegacyV1,
    /// <summary>Judge support for the answer's claims; completeness is a separate, optional dimension.</summary>
    SupportFocusedV2
}

/// <summary>One dimension's inclusion, weight, requiredness and effect on the composite verdict.</summary>
public sealed record GroundednessDimensionOptions(
    double Weight, bool Enabled = true, bool Required = true,
    ComponentFailureEffect OnFailure = ComponentFailureEffect.Fail);

/// <summary>Immutable groundedness policy. Disabled dimensions are not invoked and cannot veto the result.</summary>
public sealed record GroundednessOptions
{
    /// <summary>The versioned interpretation of the dimensions.</summary>
    public GroundednessPolicy Policy { get; init; } = GroundednessPolicy.SupportFocusedV2;
    /// <summary>Support for each factual claim in the answer.</summary>
    public GroundednessDimensionOptions ClaimSupport { get; init; } = new(0.50);
    /// <summary>Contradictions between the answer and its evidence.</summary>
    public GroundednessDimensionOptions ClaimContradicted { get; init; } = new(0.30);
    /// <summary>Accuracy of citations; absence is permitted unless RequireCitations is set.</summary>
    public GroundednessDimensionOptions CitationAccuracy { get; init; } = new(0.20);
    /// <summary>Optional answer completeness in v2; context coverage in the legacy policy.</summary>
    public GroundednessDimensionOptions Completeness { get; init; } = new(0.25, Enabled: false, OnFailure: ComponentFailureEffect.Warn);
    /// <summary>Whether absent citations fail the citation dimension.</summary>
    public bool RequireCitations { get; init; }
    /// <summary>Whether absent context makes every active dimension unmeasured before a judge call.</summary>
    public bool RequireContext { get; init; } = true;
    /// <summary>Recommended policy: short, fully supported answers need not reproduce unrelated source material.</summary>
    public static GroundednessOptions SupportFocused => new();
    /// <summary>The original policy, retained for existing callers and historical comparisons.</summary>
    public static GroundednessOptions Legacy => new()
    {
        Policy = GroundednessPolicy.LegacyV1, ClaimSupport = new(0.30), ClaimContradicted = new(0.25),
        CitationAccuracy = new(0.20), Completeness = new(0.25, OnFailure: ComponentFailureEffect.Warn), RequireContext = false
    };

    internal void Validate()
    {
        if (Policy == GroundednessPolicy.LegacyV1 && RequireCitations)
            throw new ArgumentException("Expected: SupportFocusedV2 when citations are required. Actual: LegacyV1 permits absent citations. " +
                "Suggestions: use SupportFocused with RequireCitations. Because: the legacy rubric must retain its original meaning.");
        var dimensions = new[] { ClaimSupport, ClaimContradicted, CitationAccuracy, Completeness };
        if (!Enum.IsDefined(Policy) || dimensions.Any(d => d is null || !double.IsFinite(d.Weight) || d.Weight < 0 || !Enum.IsDefined(d.OnFailure)))
            throw new ArgumentException("Expected: defined policies and finite nonnegative dimension weights. Actual: invalid groundedness options. " +
                "Suggestions: start with SupportFocused or Legacy and change individual dimensions. Because: invalid weights cannot define an instrument.");
        var total = dimensions.Where(d => d.Enabled).Sum(d => d.Weight);
        if (!double.IsFinite(total) || total <= 0)
            throw new ArgumentException("Expected: a positive finite active weight total. Actual: no usable active weights. " +
                "Suggestions: enable at least one dimension with positive weight. Because: an empty denominator is not a score.");
        if (RequireCitations && !CitationAccuracy.Enabled)
            throw new ArgumentException("Expected: an enabled citation dimension when citations are required. Actual: citation checks disabled. " +
                "Suggestions: enable CitationAccuracy. Because: a disabled check cannot enforce a requirement.");
    }
}
