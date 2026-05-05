// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;

namespace ECS2026MAF.Evals;

/// <summary>
/// Persists lightweight eval snapshots to <c>.AgentEval/ECS2026MAF_Evals/</c> under
/// the solution root so Eval03 can compare results without re-running any eval.
///
/// Storage format: indented JSON (one file per key).
/// This is NOT the standard AgentEval exporter format (IResultExporter / JUnit XML /
/// Markdown). Those exporters are for CI pipelines; this store is a fast in-process
/// cache for cross-eval hypothesis comparisons.
///
/// Location: &lt;solution-root&gt;/.AgentEval/ECS2026MAF_Evals/{key}.json
/// The .AgentEval/ folder is created automatically if it does not exist.
/// Add it to .gitignore to avoid committing evaluation artefacts.
/// </summary>
public static class EvalResultStore
{
    private static readonly string StorePath = FindStorePath();

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    /// <summary>
    /// Walks up from the running assembly's directory until it finds a directory
    /// containing <c>AgentEval.sln</c> or <c>AGENTS.md</c> (solution root).
    /// Falls back to <c>Directory.GetCurrentDirectory()</c> if not found.
    /// </summary>
    private static string FindStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0
             || dir.GetFiles("AGENTS.md").Length > 0)
                return Path.Combine(dir.FullName, ".AgentEval", "ECS2026MAF_Evals");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), ".AgentEval", "ECS2026MAF_Evals");
    }

    // ── EvalSnapshot (single-run) ─────────────────────────────────────────────

    public static void Save(string key, EvalSnapshot snapshot)
    {
        Directory.CreateDirectory(StorePath);
        var path = Path.Combine(StorePath, $"{key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts));
    }

    public static EvalSnapshot? Load(string key)
    {
        var path = Path.Combine(StorePath, $"{key}.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<EvalSnapshot>(File.ReadAllText(path), JsonOpts);
    }

    // ── StochasticSnapshot (multi-run) ────────────────────────────────────────

    public static void SaveStochastic(string key, StochasticSnapshot snapshot)
    {
        Directory.CreateDirectory(StorePath);
        var path = Path.Combine(StorePath, $"{key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts));
    }

    public static StochasticSnapshot? LoadStochastic(string key)
    {
        var path = Path.Combine(StorePath, $"{key}.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<StochasticSnapshot>(File.ReadAllText(path), JsonOpts);
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    public static bool Exists(string key) =>
        File.Exists(Path.Combine(StorePath, $"{key}.json"));

    /// <summary>Returns a human-readable age string for the stored snapshot.</summary>
    public static string GetAge(string key)
    {
        var path = Path.Combine(StorePath, $"{key}.json");
        if (!File.Exists(path)) return "never";
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return age.TotalMinutes < 60
            ? $"{(int)age.TotalMinutes} min ago"
            : $"{(int)age.TotalHours} hr ago";
    }

    /// <summary>Returns the store directory path (useful for informational output).</summary>
    public static string StorageLocation => StorePath;
}

/// <summary>Serialisable snapshot of a single eval run for comparison.</summary>
public record EvalSnapshot
{
    public string Architecture    { get; init; } = "";  // "Single Agent" / "Workflow"
    public string Label           { get; init; } = "";
    public int    LlmScore        { get; init; }         // LLM holistic score 0-100
    public int    CriteriaScore   { get; init; }         // metCount * 100 / total
    public int    CriteriaMetCount { get; init; }
    public int    CriteriaTotal   { get; init; }
    public int    ToolCallCount   { get; init; }
    public int    BookFlightCount { get; init; }
    public bool   Passed          { get; init; }
    public long   DurationMs      { get; init; }
    public DateTime RunAt         { get; init; } = DateTime.UtcNow;
    public List<CriterionSnapshot> CriteriaDetails { get; init; } = [];

    public int MissedCount => CriteriaTotal - CriteriaMetCount;
}

/// <summary>Per-criterion snapshot embedded in <see cref="EvalSnapshot"/>.</summary>
public record CriterionSnapshot(string Name, bool Met, string Explanation);

/// <summary>
/// Serialisable snapshot of a stochastic (multi-run) evaluation for spread comparison.
/// Stored by Eval04 (agent) and Eval05 (workflow); loaded and compared by Eval03.
/// </summary>
public record StochasticSnapshot
{
    public string Architecture { get; init; } = "";  // "Single Agent" / "Workflow (4 agents)"
    public string Label        { get; init; } = "";
    public int    Runs         { get; init; }
    public double PassRate     { get; init; }
    public bool   Passed       { get; init; }
    public int    PassedCount  { get; init; }
    public int    MinScore     { get; init; }
    public int    MaxScore     { get; init; }
    public double MeanScore    { get; init; }
    public List<int> Scores    { get; init; } = [];
    public DateTime RunAt      { get; init; } = DateTime.UtcNow;

    /// <summary>Score spread (max – min). Lower spread = more deterministic.</summary>
    [JsonIgnore]
    public int Spread => MaxScore - MinScore;
}

