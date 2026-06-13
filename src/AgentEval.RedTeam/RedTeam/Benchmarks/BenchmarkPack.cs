// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam.Benchmarks;

/// <summary>
/// Metadata for an external red-team benchmark pack (e.g. HarmBench, JailbreakBench, CyberSecEval). <b>Metadata only —
/// AgentEval bundles NO benchmark data.</b> The probe content is fetched on demand by <see cref="PackDownloader"/>
/// from <see cref="DataUrl"/> and only when the caller has explicitly accepted the pack's license
/// (CLI <c>--accept-license</c>) — most of these datasets contain harmful/offensive content by design.
/// </summary>
/// <remarks>
/// <see cref="DataUrl"/> must serve the importer's JSON seed-prompt schema (a JSON array of
/// <c>{ prompt, id?, technique?, source?, license?, expectedTokens? }</c>). Several upstream benchmarks publish a
/// different native format (CSV / parquet / custom JSON); for those, point <see cref="DataUrl"/> at a normalized export
/// or supply a dedicated <see cref="Importers.IProbeDatasetImporter"/>. Imported probes without an expected-token oracle
/// are Inconclusive unless an LLM judge (<c>--judge</c>) is configured — never a fabricated verdict.
/// </remarks>
public sealed record BenchmarkPack
{
    /// <summary>Short pack name used on the CLI (e.g. <c>"HarmBench"</c>); matched case-insensitively.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description of what the pack tests.</summary>
    public required string Description { get; init; }

    /// <summary>SPDX-style license identifier of the upstream dataset (e.g. <c>"MIT"</c>).</summary>
    public required string License { get; init; }

    /// <summary>Link to the upstream license / terms (surfaced to the user before download).</summary>
    public required string LicenseUrl { get; init; }

    /// <summary>Project home page (for attribution and provenance).</summary>
    public required string HomeUrl { get; init; }

    /// <summary>URL the probe data is fetched from. Must serve the importer's JSON seed-prompt schema.</summary>
    public required string DataUrl { get; init; }

    /// <summary>OWASP LLM Top-10 id the imported set is classified under (defaults to LLM01).</summary>
    public string OwaspLlmId { get; init; } = "LLM01";

    /// <summary>Whether the caller must accept the license before download (true for harmful-content packs).</summary>
    public bool RequiresLicenseAcceptance { get; init; } = true;
}
