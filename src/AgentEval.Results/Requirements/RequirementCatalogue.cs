// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Results.Requirements;

/// <summary>An explicit selector of case result rows, using the AEF summary's exact lane/metric/path binding.</summary>
/// <param name="Lane">Exact lane.</param>
/// <param name="Metric">Exact metric key.</param>
/// <param name="Path">Exact AEF result path; never a glob or inferred metric name.</param>
/// <param name="SuiteRef">Optional suite reference.</param>
/// <param name="SuiteDigest">Optional exact suite digest.</param>
/// <param name="Deployment">Optional exact deployment.</param>
public sealed record RequirementSelector(string Lane, string Metric, string Path,
    string? SuiteRef, string? SuiteDigest, string? Deployment);

/// <summary>A typed threshold or measured-state acceptance condition.</summary>
/// <param name="Kind">threshold or state.</param>
/// <param name="Operator">The numeric comparison, for a threshold.</param>
/// <param name="Value">The numeric threshold.</param>
/// <param name="States">Accepted measured states for a state condition.</param>
public sealed record RequirementAcceptance(string Kind, string? Operator, double? Value, IReadOnlyList<string> States);

/// <summary>Explicit mixed-evidence aggregation. Missing required observations yield not_measured.</summary>
/// <param name="Kind">all, any or fraction.</param>
/// <param name="Minimum">Required fraction, only for fraction.</param>
public sealed record RequirementAggregation(string Kind, double? Minimum);

/// <summary>A validated external requirement. No threshold, freshness or exception authority is inferred.</summary>
/// <param name="Id">Exact namespace:name identifier.</param>
/// <param name="Description">Optional human description.</param>
/// <param name="Selector">Which evidence rows the requirement uses.</param>
/// <param name="Acceptance">Acceptance condition.</param>
/// <param name="Aggregation">How multiple observations combine.</param>
/// <param name="FreshnessSeconds">Maximum evidence age in seconds.</param>
/// <param name="AllowEffectiveOverrides">Whether a state rule consumes overlay-effective states.</param>
public sealed record RequirementDefinition(string Id, string? Description, RequirementSelector Selector,
    RequirementAcceptance Acceptance, RequirementAggregation Aggregation, long FreshnessSeconds, bool AllowEffectiveOverrides);

/// <summary>A validated, versioned catalogue outside sealed AEF runs.</summary>
public sealed class RequirementCatalogue
{
    internal RequirementCatalogue(string digest, IReadOnlyList<RequirementDefinition> requirements)
    {
        Digest = digest;
        Requirements = requirements;
    }

    /// <summary>Catalogue contract version, independent of the AEF version.</summary>
    public string SchemaVersion => "1.0";
    /// <summary>SHA-256 of the exact source bytes, including whitespace and any YAML representation.</summary>
    public string Digest { get; }
    /// <summary>Validated requirements in author order.</summary>
    public IReadOnlyList<RequirementDefinition> Requirements { get; }
}

/// <summary>Invalid catalogue input, with a location and corrective suggestion.</summary>
public sealed class RequirementCatalogueException(string message) : FormatException(message);

/// <summary>Validates the external catalogue contract and constructs typed definitions.</summary>
public interface IRequirementCatalogueReader
{
    /// <summary>Reads strict UTF-8 JSON. A format adapter may supply original source bytes for exact source identity.</summary>
    /// <param name="utf8Json">The catalogue as UTF-8 JSON.</param>
    /// <param name="sourceBytes">The source represented by that JSON; omit for a JSON source.</param>
    RequirementCatalogue Read(ReadOnlyMemory<byte> utf8Json, ReadOnlyMemory<byte>? sourceBytes = null);
}
