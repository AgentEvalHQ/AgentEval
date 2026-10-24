// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Persists lightweight eval snapshots under the canonical AgentEval workspace
/// so Eval03 can compare results without re-running any eval.
///
/// Storage format: indented JSON (one file per key).
/// This is NOT the standard AgentEval exporter format (IResultExporter / JUnit XML /
/// Markdown). Those exporters are for CI pipelines; this store is a fast in-process
/// snapshot of demo-specific data for cross-eval hypothesis comparisons.
///
/// Location: &lt;workspace-root&gt;/.agenteval/samples/AgentEval.TravelDemo.Evals/snapshots/{key}.json
/// The folder is created automatically if it does not exist and is gitignored
/// via the repo-wide .agenteval/ rule.
/// </summary>
public static class EvalResultStore
{
    private static readonly string StorePath = FindStorePath();

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    /// <summary>
    /// Walks up from the running assembly's directory until it finds a directory
    /// containing <c>AgentEval.sln</c> or <c>AGENTS.md</c> (workspace root).
    /// Falls back to <c>Directory.GetCurrentDirectory()</c> if not found.
    /// </summary>
    private static string FindStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0
             || dir.GetFiles("AGENTS.md").Length > 0)
                return Path.Combine(dir.FullName, ".agenteval", "samples", "AgentEval.TravelDemo.Evals", "snapshots");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), ".agenteval", "samples", "AgentEval.TravelDemo.Evals", "snapshots");
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

    /// <summary>
    /// The provider tag of the host this process runs on — what a snapshot records as its provenance. With
    /// <see cref="CurrentModel"/> it identifies the agent AND the judge: TravelDemo.Evals grades on the same
    /// provider and model it runs (<c>Config.CreateChatClient()</c>). Recorded whatever the console shows: hiding
    /// the model (<c>Config.ShowModel</c>) is a display rule, not a provenance rule.
    /// </summary>
    public static string CurrentProviderTag => Config.Settings.ProviderTag;

    /// <summary>The model this process runs, <c>--model</c> override included.</summary>
    public static string CurrentModel => Config.Model;
}

/// <summary>Whether two snapshots came from the same model on the same host.</summary>
public enum ProvenanceMatch
{
    /// <summary>Both record the same <c>model@provider</c>.</summary>
    Same,

    /// <summary>Both record one, and they differ: the comparison is not between architectures alone.</summary>
    Different,

    /// <summary>At least one records none (saved before provenance was recorded): nobody knows.</summary>
    Unknown,
}

/// <summary>Provenance helpers for the snapshot records.</summary>
public static class Provenance
{
    /// <summary><c>model@provider</c>, or null when the snapshot does not record both.</summary>
    public static string? Identity(string? model, string? providerTag) =>
        string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(providerTag) ? null : $"{model}@{providerTag}";

    /// <summary>Compares two identities; a missing one is <see cref="ProvenanceMatch.Unknown"/>, never a match.</summary>
    public static ProvenanceMatch Compare(string? a, string? b) =>
        a is null || b is null ? ProvenanceMatch.Unknown
        : string.Equals(a, b, StringComparison.Ordinal) ? ProvenanceMatch.Same
        : ProvenanceMatch.Different;
}

/// <summary>
/// A run's verdict over every dimension it checked — the owner's rule: a failed structure / tool / order / policy /
/// response check is <see cref="Fail"/> whatever the judge said, a failed quality gate on sound checks is
/// <see cref="Warn"/>, and anything not measured is <see cref="Incomplete"/>, never a <see cref="Pass"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunVerdict
{
    Pass,
    Warn,
    Fail,
    Incomplete,
}

/// <summary>The one rule Eval 01 and Eval 02 both print and save.</summary>
public static class RunVerdicts
{
    /// <param name="checksPassed">The deterministic checks (structure, tools, order, policy, response); null when they could not look.</param>
    /// <param name="qualityPassed">The judge's quality gate; null when it was not measured.</param>
    public static RunVerdict Combine(bool? checksPassed, bool? qualityPassed) => (checksPassed, qualityPassed) switch
    {
        (false, _)    => RunVerdict.Fail,
        (null, _)     => RunVerdict.Incomplete,
        (true, true)  => RunVerdict.Pass,
        (true, false) => RunVerdict.Warn,
        (true, null)  => RunVerdict.Incomplete,
    };
}

/// <summary>Serialisable snapshot of a single eval run for comparison.</summary>
/// <remarks>
/// <see cref="ProviderTag"/> and <see cref="Model"/> are nullable so a snapshot saved before they existed still
/// loads; it then compares as <see cref="ProvenanceMatch.Unknown"/>. <see cref="StructurePassed"/> and
/// <see cref="Result"/> likewise: absent, they read as null — not recorded — and Eval 3 gives no verdict on them.
/// <see cref="Passed"/> is the QUALITY GATE only (the judge's score against the pass mark): a run can pass it and
/// still FAIL on its tools or structure, which is what <see cref="StructurePassed"/> records.
/// </remarks>
public record EvalSnapshot
{
    public string Architecture    { get; init; } = "";  // "Single Agent" / "Workflow"
    public string Label           { get; init; } = "";
    public string? ProviderTag    { get; init; }         // azure / bitdeer / openai / foundry / openai-compatible
    public string? Model          { get; init; }         // the model (Azure: deployment) the agent AND the judge ran on
    public int    LlmScore        { get; init; }         // LLM holistic score 0-100
    public int    CriteriaScore   { get; init; }         // metCount * 100 / total
    public int    CriteriaMetCount { get; init; }
    public int    CriteriaTotal   { get; init; }
    public int    ToolCallCount   { get; init; }
    public int    BookFlightCount { get; init; }
    public bool   Passed          { get; init; }         // the quality gate only (judge score ≥ pass mark)
    public bool?  StructurePassed { get; init; }         // structure / tool / order / policy / response checks; null = not recorded
    public RunVerdict? Result     { get; init; }         // the run's verdict over both (RunVerdicts.Combine); null = not recorded
    public long   DurationMs      { get; init; }
    public DateTime RunAt         { get; init; } = DateTime.UtcNow;
    public List<CriterionSnapshot> CriteriaDetails { get; init; } = [];

    public int MissedCount => CriteriaTotal - CriteriaMetCount;

    /// <summary><c>model@provider</c>, or null for a snapshot that does not record it.</summary>
    [JsonIgnore]
    public string? ModelIdentity => Provenance.Identity(Model, ProviderTag);
}

/// <summary>Per-criterion snapshot embedded in <see cref="EvalSnapshot"/>.</summary>
public record CriterionSnapshot(string Name, bool Met, string Explanation);

/// <summary>
/// Serialisable snapshot of a stochastic (multi-run) evaluation for spread comparison.
/// Stored by Eval04 (agent) and Eval05 (workflow); loaded and compared by Eval03.
/// </summary>
/// <remarks>
/// A run that failed on the model call or the judge is NOT a score of 0: it is counted in <see cref="ErroredRuns"/>
/// and left out of <see cref="PassRate"/>, <see cref="PassedCount"/> and the scores. A run that WAS measured but has
/// no judge score — the workflow produced no output, or its structure failed while the judge gave no verdict — is a
/// FAILED run in <see cref="PassRate"/> and is counted in <see cref="UnscoredRuns"/>: the spread covers the other runs
/// only, so Eval 3 gives no consistency verdict on it. <see cref="ProviderTag"/>, <see cref="Model"/>,
/// <see cref="ErroredRuns"/> and <see cref="UnscoredRuns"/> are absent from snapshots saved before they existed and
/// then read as null / 0 / null (an unrecorded unscored count is unknown, not zero).
/// </remarks>
public record StochasticSnapshot
{
    public string Architecture { get; init; } = "";  // "Single Agent" / "Workflow (4 agents)"
    public string Label        { get; init; } = "";
    public string? ProviderTag { get; init; }         // azure / bitdeer / openai / foundry / openai-compatible
    public string? Model       { get; init; }         // the model (Azure: deployment) the agent AND the judge ran on
    public int    Runs         { get; init; }         // runs attempted
    public int    ErroredRuns  { get; init; }         // runs not measured (model call or judge failed)
    public int?   UnscoredRuns { get; init; }         // measured runs with no judge score (failed, not in the spread); null = not recorded
    public double PassRate     { get; init; }         // over the measured runs
    public bool   Passed       { get; init; }         // a clean pass: threshold met AND no run errored
    public int    PassedCount  { get; init; }
    public int    MinScore     { get; init; }
    public int    MaxScore     { get; init; }
    public double MeanScore    { get; init; }
    public List<int> Scores    { get; init; } = [];
    public DateTime RunAt      { get; init; } = DateTime.UtcNow;

    /// <summary>Score spread (max – min). Lower spread = more deterministic.</summary>
    [JsonIgnore]
    public int Spread => MaxScore - MinScore;

    /// <summary>Runs that produced a verdict.</summary>
    [JsonIgnore]
    public int MeasuredRuns => Runs - ErroredRuns;

    /// <summary>Runs the spread is computed over (those with a judge score).</summary>
    [JsonIgnore]
    public int ScoredRuns => Scores.Count;

    /// <summary><c>model@provider</c>, or null for a snapshot that does not record it.</summary>
    [JsonIgnore]
    public string? ModelIdentity => Provenance.Identity(Model, ProviderTag);
}

