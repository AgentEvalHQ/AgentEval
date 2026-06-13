// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam.Benchmarks;

/// <summary>
/// The built-in catalog of known external benchmark packs (metadata only — no data is bundled). Resolve a pack by name
/// and hand it to <see cref="PackDownloader"/>, which downloads + imports it behind the <c>--accept-license</c> gate.
/// </summary>
/// <remarks>
/// The <see cref="BenchmarkPack.DataUrl"/>s point at each project's canonical upstream data file (verified
/// 2026-06-13), and <see cref="BenchmarkPack.Format"/> + <see cref="BenchmarkPack.PromptField"/> tell the downloader how
/// to parse each one (HarmBench/JBB are CSV; CyberSecEval is JSON). We deliberately ship NO data and require explicit
/// license acceptance — these datasets contain harmful content by design. Upstream paths can move; if a pack 404s, pass
/// the current raw URL directly to <c>--pack &lt;url&gt;</c>.
/// </remarks>
public static class PackCatalog
{
    /// <summary>All known benchmark packs (metadata only).</summary>
    public static IReadOnlyList<BenchmarkPack> All { get; } =
    [
        new BenchmarkPack
        {
            Name = "HarmBench",
            Description = "Standardized harmful-behavior prompts (Center for AI Safety).",
            License = "MIT",
            LicenseUrl = "https://github.com/centerforaisafety/HarmBench/blob/main/LICENSE",
            HomeUrl = "https://www.harmbench.org/",
            DataUrl = "https://raw.githubusercontent.com/centerforaisafety/HarmBench/main/data/behavior_datasets/harmbench_behaviors_text_all.csv",
            Format = "csv",
            PromptField = "Behavior",
            OwaspLlmId = "LLM01",
        },
        new BenchmarkPack
        {
            Name = "JailbreakBench",
            Description = "JBB-Behaviors harmful-behavior prompt set (JailbreakBench).",
            License = "MIT",
            LicenseUrl = "https://github.com/JailbreakBench/jailbreakbench/blob/main/LICENSE",
            HomeUrl = "https://jailbreakbench.github.io/",
            DataUrl = "https://huggingface.co/datasets/JailbreakBench/JBB-Behaviors/resolve/main/data/harmful-behaviors.csv",
            Format = "csv",
            PromptField = "Goal",
            OwaspLlmId = "LLM01",
        },
        new BenchmarkPack
        {
            Name = "CyberSecEval",
            Description = "Prompt-injection security prompts (Meta PurpleLlama CyberSecEval).",
            License = "MIT",
            LicenseUrl = "https://github.com/meta-llama/PurpleLlama/blob/main/LICENSE",
            HomeUrl = "https://meta-llama.github.io/PurpleLlama/",
            DataUrl = "https://raw.githubusercontent.com/meta-llama/PurpleLlama/main/CybersecurityBenchmarks/datasets/prompt_injection/prompt_injection.json",
            Format = "json",
            PromptField = "test_case_prompt",
            OwaspLlmId = "LLM01",
        },
    ];

    /// <summary>Builds an ad-hoc pack for an arbitrary user-supplied data URL (<c>--pack &lt;url&gt;</c>). Inferred
    /// format: <c>.csv</c> ⇒ csv (prompt column <c>"prompt"</c>), else json. The user owns the source, so no license
    /// gate is imposed.</summary>
    public static BenchmarkPack ForUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        // L15: infer the format from the URL PATH only — a query/fragment ("...harmful-behaviors.csv?ref=main")
        // must not defeat the .csv suffix check.
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var isCsv = path.TrimEnd().EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        return new BenchmarkPack
        {
            Name = "custom",
            Description = "User-supplied benchmark pack",
            License = "user-supplied",
            LicenseUrl = url,
            HomeUrl = url,
            DataUrl = url,
            Format = isCsv ? "csv" : "json",
            PromptField = "prompt",
            RequiresLicenseAcceptance = false,
        };
    }

    /// <summary>Find a pack by name (case-insensitive), or <c>null</c> if unknown.</summary>
    public static BenchmarkPack? Find(string name)
        => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Comma-separated list of known pack names (for help text / error messages).</summary>
    public static string Names => string.Join(", ", All.Select(p => p.Name));
}
