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
/// <see cref="Format"/> + <see cref="PromptField"/> select the importer and the prompt column/key, so a pack's native
/// JSON-or-CSV format (e.g. HarmBench's <c>Behavior</c> CSV column, JBB's <c>Goal</c>, CyberSecEval's
/// <c>test_case_prompt</c> JSON key) is parsed directly — no manual normalization needed. Imported probes carry no
/// expected-token oracle, so they are Inconclusive unless an LLM judge (<c>--judge</c>) is configured — never a
/// fabricated verdict.
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

    /// <summary>URL the probe data is fetched from.</summary>
    public required string DataUrl { get; init; }

    /// <summary>Upstream data format: <c>"json"</c> (array of objects) or <c>"csv"</c>. Selects the importer.</summary>
    public string Format { get; init; } = "json";

    /// <summary>The object key (JSON) or column name (CSV) holding the prompt text. Defaults to <c>"prompt"</c> (native
    /// seed schema); set per pack for upstream formats (HarmBench <c>Behavior</c>, JBB <c>Goal</c>, CyberSecEval
    /// <c>test_case_prompt</c>).</summary>
    public string PromptField { get; init; } = "prompt";

    /// <summary>OWASP LLM Top-10 id the imported set is classified under (defaults to LLM01).</summary>
    public string OwaspLlmId { get; init; } = "LLM01";

    /// <summary>Whether the caller must accept the license before download (true for harmful-content packs).</summary>
    public bool RequiresLicenseAcceptance { get; init; } = true;
}
