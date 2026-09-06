// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Persists lightweight eval snapshots under the canonical AgentEval workspace so a later run can
/// compare results without re-running anything.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>AgentEval.TravelDemo.Evals.EvalResultStore</c> with one correction.
/// TravelDemo's <c>EvalSnapshot.MissedCount</c> is a COMPUTED property with no
/// <c>[JsonIgnore]</c>: it is serialised on write and then silently dropped on read, because a
/// get-only expression-bodied property has no setter for the deserialiser to call. The value in
/// the file therefore looks authoritative and is, on the round trip, always zero. Every computed
/// property in this file carries <c>[JsonIgnore]</c>.
/// </para>
/// <para>
/// Storage format: indented JSON, one file per key, under
/// <c>&lt;workspace-root&gt;/.agenteval/samples/Galaxus.RecommendationAgent.Evals/snapshots/</c>.
/// This is NOT the standard AgentEval exporter format (IResultExporter / JUnit XML / Markdown) —
/// those are for CI pipelines; this is a fast in-process snapshot of demo-specific data.
/// </para>
/// </remarks>
public static class EvalResultStore
{
    private static readonly string StorePath = FindStorePath();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // NaN is a legitimate value throughout this suite — it is how an EMPTY DENOMINATOR is
        // represented, and representing it as 0 or 1 is the flattering failure the graders exist
        // to avoid. Without this the serialiser throws on the first unscorable persona and the
        // whole snapshot is lost, which would turn "we could not score this one" into "we have no
        // record of the run at all".
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Snapshot key for Eval 01.</summary>
    public const string IntegrityKey = "eval01_integrity";

    /// <summary>Snapshot key for Eval 02.</summary>
    public const string CoverageKey = "eval02_coverage_ab";

    /// <summary>Snapshot key for the negative-control run.</summary>
    public const string ControlsKey = "eval03_controls";

    private static readonly List<string> WrittenKeys = [];

    /// <summary>
    /// Every snapshot key written by THIS process, in write order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a ledger and not a list of expected keys.</b> The `--ci --dry-run` banner used to
    /// print <i>"no model was called and no snapshot was written"</i> unconditionally, and it was
    /// false: Evals 03 and 04 call no model, so the CI chain passes them no <c>dryRun</c> argument,
    /// so they run for real inside a dry run and persist. MEASURED 2026-09-06
    /// (<c>MEASUREMENT_STATUS</c> §24.7 item 1): <c>eval03_controls</c> and <c>eval04_injection</c>
    /// moved at 01:26:14, inside a <c>00-ci-dryrun-concept</c> that ran 01:26:12–01:26:19. The
    /// WRITES are correct — both are real, model-free measurements — and the CLAIM was the defect.
    /// </para>
    /// <para>
    /// ⚠ The banner now reads this. A hand-maintained list of "the two evals that persist under a
    /// dry run" would be a second claim about the code, and this repository's own §2.4 records what
    /// happens to those: the enumerated call-site list in ADR-030 Slice 1.2 was wrong by 20 %.
    /// A run reporting its own ledger cannot drift from it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> KeysWrittenThisRun
    {
        get { lock (WrittenKeys) return [.. WrittenKeys]; }
    }

    /// <summary>
    /// The subset of <see cref="KeysWrittenThisRun"/> whose file is still on disk.
    /// </summary>
    /// <remarks>
    /// What the <c>--ci --dry-run</c> banner reports, and the reason is falsifiability: a reader
    /// who is told a snapshot was written can go and look at it. A key whose file the run itself
    /// removed again — Eval 03's write-ledger probe is the only one today — names a file that is
    /// not there, and a banner naming a missing file is a new version of the defect it replaced.
    /// </remarks>
    public static IReadOnlyList<string> SnapshotsWrittenThisRun =>
        [.. KeysWrittenThisRun.Where(Exists)];

    /// <summary>Records a snapshot write in <see cref="KeysWrittenThisRun"/>.</summary>
    /// <remarks>
    /// Called by <see cref="Write{T}"/> and by <c>OfflineSnapshotStore.Save</c> — the suite's two
    /// write chokepoints. A third would have to call it too, and the control that pins the ledger
    /// says so.
    /// </remarks>
    /// <param name="key">The key just written.</param>
    internal static void RecordWrite(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (WrittenKeys)
        {
            if (!WrittenKeys.Contains(key, StringComparer.Ordinal)) WrittenKeys.Add(key);
        }
    }

    private static string FindStorePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("AgentEval.sln").Length > 0 || dir.GetFiles("AGENTS.md").Length > 0)
                return Path.Combine(dir.FullName, ".agenteval", "samples", "Galaxus.RecommendationAgent.Evals", "snapshots");
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), ".agenteval", "samples", "Galaxus.RecommendationAgent.Evals", "snapshots");
    }

    /// <summary>Saves an Eval 01 snapshot.</summary>
    /// <param name="key">Storage key, normally <see cref="IntegrityKey"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void SaveIntegrity(string key, IntegritySnapshot snapshot) => Write(key, snapshot);

    /// <summary>Loads an Eval 01 snapshot, or null when it has never been written.</summary>
    /// <param name="key">Storage key.</param>
    public static IntegritySnapshot? LoadIntegrity(string key) => Read<IntegritySnapshot>(key);

    /// <summary>Saves an Eval 02 snapshot.</summary>
    /// <param name="key">Storage key, normally <see cref="CoverageKey"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void SaveCoverage(string key, CoverageSnapshot snapshot) => Write(key, snapshot);

    /// <summary>Loads an Eval 02 snapshot, or null when it has never been written.</summary>
    /// <param name="key">Storage key.</param>
    public static CoverageSnapshot? LoadCoverage(string key) => Read<CoverageSnapshot>(key);

    /// <summary>Saves the negative-control snapshot.</summary>
    /// <param name="key">Storage key, normally <see cref="ControlsKey"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void SaveControls(string key, ControlSnapshot snapshot) => Write(key, snapshot);

    /// <summary>Loads the negative-control snapshot, or null when it has never been written.</summary>
    /// <param name="key">Storage key.</param>
    public static ControlSnapshot? LoadControls(string key) => Read<ControlSnapshot>(key);

    private static void Write<T>(string key, T snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Directory.CreateDirectory(StorePath);

        // ⚠ A paid run's record is never silently overwritten. The 2026-09-04 Eval 02 snapshot
        // cost $18.56 and is the only live coverage record this suite has; the own-k re-read is
        // built on it. The previous file is moved aside under its own last-write time before the
        // new one lands, so a re-run ADDS a record rather than replacing the one it is compared
        // against. Archives are dated by the file's mtime (UTC), not by the new run's clock.
        string path = Path.Combine(StorePath, $"{key}.json");
        if (File.Exists(path))
        {
            string stamp = File.GetLastWriteTimeUtc(path).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            string archive = Path.Combine(StorePath, $"{key}.{stamp}.json");
            if (!File.Exists(archive)) File.Copy(path, archive);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts));
        RecordWrite(key);
    }

    private static T? Read<T>(string key) where T : class
    {
        var path = Path.Combine(StorePath, $"{key}.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
    }

    /// <summary>True when a snapshot for that key exists on disk.</summary>
    /// <param name="key">Storage key.</param>
    public static bool Exists(string key) => File.Exists(Path.Combine(StorePath, $"{key}.json"));

    /// <summary>A human-readable age for a stored snapshot.</summary>
    /// <param name="key">Storage key.</param>
    public static string GetAge(string key)
    {
        var path = Path.Combine(StorePath, $"{key}.json");
        if (!File.Exists(path)) return "never";
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago" : $"{(int)age.TotalHours} hr ago";
    }

    /// <summary>The directory snapshots are written to.</summary>
    public static string StorageLocation => StorePath;
}

/// <summary>Serialisable snapshot of one Eval 01 run.</summary>
public sealed record IntegritySnapshot
{
    /// <summary>Which arm produced it — "Single Agent", "Broken01_HallucinatingRecommender", …</summary>
    public string Architecture { get; init; } = "";

    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Cases graded.</summary>
    public int CaseCount { get; init; }

    /// <summary>Cases with no defect of any class.</summary>
    public int CleanCaseCount { get; init; }

    /// <summary>Total <c>PresentRecommendation</c> calls.</summary>
    public int PresentedTotal { get; init; }

    /// <summary>Presentations with no per-item defect.</summary>
    public int CleanPresentedTotal { get; init; }

    /// <summary>Presentations that never paired with a tool result.</summary>
    public int UnexecutedPresentedTotal { get; init; }

    /// <summary>Soft-class clean rate, or -1 when nothing was presented (undefined, not perfect).</summary>
    public double SoftClassCleanRate { get; init; }

    /// <summary>True when no zero-tolerance class fired.</summary>
    public bool HardClean { get; init; }

    /// <summary>True when the whole gate passed.</summary>
    public bool Passed { get; init; }

    /// <summary>Wall clock across the graded turns.</summary>
    public long TotalDurationMs { get; init; }

    /// <summary>Tokens across the run, when reported.</summary>
    public int TotalTokens { get; init; }

    /// <summary>Estimated cost across the run, when computable.</summary>
    public decimal EstimatedCost { get; init; }

    /// <summary>Defect counts by class name.</summary>
    public Dictionary<string, int> DefectsByClass { get; init; } = [];

    /// <summary>Per-case detail.</summary>
    public List<IntegrityCaseSnapshot> Cases { get; init; } = [];

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;

    /// <summary>Cases that produced at least one defect. Computed — never round-tripped.</summary>
    [JsonIgnore]
    public int FailedCaseCount => CaseCount - CleanCaseCount;
}

/// <summary>Per-case row inside an <see cref="IntegritySnapshot"/>.</summary>
/// <param name="CaseId">Case id.</param>
/// <param name="Group">Defect-class group.</param>
/// <param name="PersonaId">Customer id.</param>
/// <param name="Clean">True when no defect fired.</param>
/// <param name="PresentedCount">Recommendations presented.</param>
/// <param name="Defects">Rendered defect lines.</param>
public sealed record IntegrityCaseSnapshot(
    string CaseId,
    string Group,
    string PersonaId,
    bool Clean,
    int PresentedCount,
    List<string> Defects);

/// <summary>Serialisable snapshot of one Eval 02 comparison.</summary>
public sealed record CoverageSnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Personas in the analysis set.</summary>
    public int PersonaCount { get; init; }

    /// <summary>Arm labels, in run order.</summary>
    public List<string> Arms { get; init; } = [];

    /// <summary>Mean latent coverage per arm.</summary>
    public Dictionary<string, double> MeanLatentByArm { get; init; } = [];

    /// <summary>Mean manifest coverage per arm.</summary>
    public Dictionary<string, double> MeanManifestByArm { get; init; } = [];

    /// <summary>
    /// Cross-persona forced-choice rate per arm — the share of personas whose own gold this arm's
    /// answer scored strictly highest on. Chance is exactly 1/PersonaCount.
    /// </summary>
    public Dictionary<string, double> ForcedChoiceByArm { get; init; } = [];

    /// <summary>
    /// The reference random-draw floor per persona, at the fixed k this suite prints as a
    /// reference. <b>Not the number an arm is gated against</b> — that is the per-cell
    /// <see cref="CoverageCellSnapshot.LatentFloor"/>, derived at the arm's own presentation count.
    /// </summary>
    public Dictionary<string, double> RandomFloorByPersona { get; init; } = [];

    /// <summary>Every persona-by-arm cell, scored at each arm's OWN presentation count.</summary>
    public List<CoverageCellSnapshot> Cells { get; init; } = [];

    /// <summary>
    /// The same cells scored at the DECLARED budget — every arm cut to its top
    /// <see cref="DeclaredK"/>. Empty on a snapshot written before the budget was declared.
    /// </summary>
    public List<CoverageCellSnapshot> CellsAtDeclaredK { get; init; } = [];

    /// <summary>
    /// The presentation budget the utterance declared, or 0 when it declared none. ⚠ A snapshot
    /// at 0 was produced by an agent that was never told how many items to present, and its live
    /// cells can be read only at the k it happened to choose — never at a declared one.
    /// </summary>
    public int DeclaredK { get; init; }

    /// <summary>The customer utterance every arm was given, verbatim. Empty on an older snapshot.</summary>
    public string Utterance { get; init; } = "";

    /// <summary>Cost totals per arm.</summary>
    public Dictionary<string, ArmCostSnapshot> CostByArm { get; init; } = [];

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}

/// <summary>One persona-by-arm cell of an Eval 02 comparison.</summary>
/// <param name="PersonaId">Customer id.</param>
/// <param name="Arm">Arm label.</param>
/// <param name="Latent">Latent coverage, or -1 when unscorable.</param>
/// <param name="Manifest">Manifest coverage, or -1 when unscorable.</param>
/// <param name="LatentServed">Latent tokens served.</param>
/// <param name="LatentTotal">Latent tokens in the gold set.</param>
/// <param name="PresentedCount">Recommendations presented.</param>
/// <param name="PhantomCount">Presented SKUs not in the catalogue.</param>
/// <param name="LatentFloor">
/// The random-draw floor for THIS cell, derived at k = <paramref name="PresentedCount"/>. This is
/// the number the cell is compared against; a fixed-k floor beside a variable-k answer is wrong in
/// the flattering direction exactly when the arm is most verbose.
/// </param>
/// <param name="ForcedChoice">
/// 1 when this persona's own gold scored strictly highest on this answer, 0 when it did not, NaN
/// when the comparison was undefined.
/// </param>
/// <param name="DeclaredK">The budget this cell was cut to, or 0 when scored at the arm's own k.</param>
/// <param name="PresentedBeforeCut">Items the arm emitted before any cut; -1 on an older snapshot.</param>
/// <param name="RelevantCount">Distinct new-category items carrying a latent gold token, within the scored k.</param>
/// <param name="PrecisionAtK">Relevant items over the DECLARED slots. NaN when no budget was declared.</param>
/// <param name="PrecisionFloor">Expected precision of a random draw from the eligible pool, R/N.</param>
/// <param name="KUniformAcrossReps">True when every rep folded into this cell presented the same count.</param>
/// <param name="PresentedSkusByRep">
/// Every rep's presented SKUs in the arm's own order, so the cell can be re-cut at any k later.
/// Null on a snapshot written before this was recorded — such a cell can be compared at its
/// recorded k and cannot be re-cut.
/// </param>
public sealed record CoverageCellSnapshot(
    string PersonaId,
    string Arm,
    double Latent,
    double Manifest,
    int LatentServed,
    int LatentTotal,
    int PresentedCount,
    int PhantomCount,
    double LatentFloor = double.NaN,
    double ForcedChoice = double.NaN,
    int DeclaredK = 0,
    int PresentedBeforeCut = -1,
    int RelevantCount = 0,
    double PrecisionAtK = double.NaN,
    double PrecisionFloor = double.NaN,
    bool KUniformAcrossReps = true,
    List<List<string>>? PresentedSkusByRep = null);

/// <summary>Cost totals for one arm.</summary>
/// <param name="Runs">Agent turns run.</param>
/// <param name="DurationMs">Wall clock, milliseconds.</param>
/// <param name="PromptTokens">Prompt tokens.</param>
/// <param name="CompletionTokens">Completion tokens.</param>
/// <param name="EstimatedCost">Estimated cost.</param>
public sealed record ArmCostSnapshot(
    int Runs,
    long DurationMs,
    int PromptTokens,
    int CompletionTokens,
    decimal EstimatedCost);

/// <summary>Serialisable snapshot of the negative-control run — the wiring self-check.</summary>
public sealed record ControlSnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>One row per control.</summary>
    public List<ControlRowSnapshot> Controls { get; init; } = [];

    /// <summary>True when every control failed as loudly as it was supposed to.</summary>
    public bool AllControlsTripped { get; init; }

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}

/// <summary>One control's result.</summary>
/// <param name="Name">Control type name.</param>
/// <param name="Expectation">What it was supposed to do, in one sentence.</param>
/// <param name="Observed">What it actually did.</param>
/// <param name="Tripped">True when the eval caught it — the outcome that proves the eval can fail.</param>
/// <param name="Gating">
/// True for a WIRING control, whose failure means the instrument is broken and must gate the run.
/// False for an INSTRUMENT FINDING — a fact about the corpus or the metric that is reported loudly
/// and never gated, because gating on it would create an incentive to tune the corpus until the
/// number came out right, which is the same shape as letting the artifact under test set its own bar.
/// </param>
public sealed record ControlRowSnapshot(
    string Name,
    string Expectation,
    string Observed,
    bool Tripped,
    bool Gating = true);
