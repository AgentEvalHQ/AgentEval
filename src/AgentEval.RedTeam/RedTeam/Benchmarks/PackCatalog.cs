// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam.Benchmarks;

/// <summary>
/// The built-in catalog of known external benchmark packs (metadata only — no data is bundled). Resolve a pack by name
/// and hand it to <see cref="PackDownloader"/>, which downloads + imports it behind the <c>--accept-license</c> gate.
/// </summary>
/// <remarks>
/// The <see cref="BenchmarkPack.DataUrl"/>s point at each project's canonical location. Upstream native formats vary
/// (HarmBench/JailbreakBench/CyberSecEval do not all publish the importer's JSON seed schema directly); confirm the URL
/// serves that schema, or point it at a normalized export, before relying on a pack in CI. We deliberately ship NO
/// fabricated data and require explicit license acceptance — these datasets contain harmful content by design.
/// </remarks>
public static class PackCatalog
{
    /// <summary>All known benchmark packs (metadata only).</summary>
    public static IReadOnlyList<BenchmarkPack> All { get; } =
    [
        new BenchmarkPack
        {
            Name = "HarmBench",
            Description = "Standardized harmful-behavior / jailbreak prompts (Center for AI Safety).",
            License = "MIT",
            LicenseUrl = "https://github.com/centerforaisafety/HarmBench/blob/main/LICENSE",
            HomeUrl = "https://www.harmbench.org/",
            DataUrl = "https://raw.githubusercontent.com/centerforaisafety/HarmBench/main/data/agenteval-seed.json",
            OwaspLlmId = "LLM01",
        },
        new BenchmarkPack
        {
            Name = "JailbreakBench",
            Description = "JBB-Behaviors jailbreak prompt set (JailbreakBench).",
            License = "MIT",
            LicenseUrl = "https://github.com/JailbreakBench/jailbreakbench/blob/main/LICENSE",
            HomeUrl = "https://jailbreakbench.github.io/",
            DataUrl = "https://raw.githubusercontent.com/JailbreakBench/jailbreakbench/main/data/agenteval-seed.json",
            OwaspLlmId = "LLM01",
        },
        new BenchmarkPack
        {
            Name = "CyberSecEval",
            Description = "Insecure-code / prompt-injection security prompts (Meta PurpleLlama CyberSecEval).",
            License = "MIT",
            LicenseUrl = "https://github.com/meta-llama/PurpleLlama/blob/main/LICENSE",
            HomeUrl = "https://meta-llama.github.io/PurpleLlama/",
            DataUrl = "https://raw.githubusercontent.com/meta-llama/PurpleLlama/main/CybersecurityBenchmarks/agenteval-seed.json",
            OwaspLlmId = "LLM05",
        },
    ];

    /// <summary>Find a pack by name (case-insensitive), or <c>null</c> if unknown.</summary>
    public static BenchmarkPack? Find(string name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comma-separated list of known pack names (for help text / error messages).</summary>
    public static string Names => string.Join(", ", All.Select(p => p.Name));
}
