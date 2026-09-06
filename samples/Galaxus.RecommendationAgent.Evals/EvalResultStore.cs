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

    /// <summary>Snapshot key for Eval 05.</summary>
    public const string QualityKey = "eval05_quality";

    /// <summary>Snapshot key for Eval 06.</summary>
    public const string TrajectoryKey = "eval06_trajectory";

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

    /// <summary>Saves the Eval 05 judged-quality snapshot.</summary>
    /// <param name="key">Storage key, normally <see cref="QualityKey"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void SaveQuality(string key, QualitySnapshot snapshot) => Write(key, snapshot);

    /// <summary>Loads the Eval 05 snapshot, or null when it has never been written.</summary>
    /// <param name="key">Storage key.</param>
    public static QualitySnapshot? LoadQuality(string key) => Read<QualitySnapshot>(key);

    /// <summary>Saves the Eval 06 tool-trajectory snapshot.</summary>
    /// <param name="key">Storage key, normally <see cref="TrajectoryKey"/>.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void SaveTrajectory(string key, TrajectorySnapshot snapshot) => Write(key, snapshot);

    /// <summary>Loads the Eval 06 snapshot, or null when it has never been written.</summary>
    /// <param name="key">Storage key.</param>
    public static TrajectorySnapshot? LoadTrajectory(string key) => Read<TrajectorySnapshot>(key);

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

        File.WriteAllText(path, Render(snapshot));
        RecordWrite(key);
    }

    /// <summary>
    /// The exact bytes <see cref="Write{T}"/> puts on disk, minus the file handle. Plan item 7.1 /
    /// ADR-031 S1's second clause attaches the run's provenance here, at the single chokepoint, so
    /// that no eval and no future snapshot record type has to remember to carry it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Internal so a control can EXECUTE the byte-producing seam rather than assert about its
    /// source text.</b> §55.5 is the record of why: a source-text check that <c>Provenance</c> is
    /// "mentioned" would be satisfied by the comment explaining it. The only thing outside this
    /// method that <c>Write</c> does is the <c>File.WriteAllText</c>, and the archive-first rule
    /// above it.
    /// </remarks>
    /// <typeparam name="T">The snapshot record type.</typeparam>
    /// <param name="snapshot">The snapshot.</param>
    internal static string Render<T>(T snapshot) =>
        SnapshotProvenance.OfThisProcess().Attach(JsonSerializer.Serialize(snapshot, JsonOpts), JsonOpts);

    /// <summary>
    /// The store's own serialiser options, so a control can replay a stored document with the
    /// settings that wrote it instead of a hand-built copy of them.
    /// </summary>
    /// <remarks>
    /// ⚠ A hand-built copy is the bar-supplied shape the Wave 2 review found in ADR-031 S2's own
    /// test: it stays green when the real settings change. The NaN clause of
    /// <c>EverySnapshotSaysWhatProducedIt</c> is only meaningful because it reads THIS object.
    /// </remarks>
    internal static JsonSerializerOptions StorageJsonOptions => JsonOpts;

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
/// <param name="ModelFreeRuns">
/// Turns recorded with NO metrics object — a deterministic arm's turns. Plan item 8.3: without this
/// the stored record cannot tell an arm that genuinely spent nothing from an arm whose usage never
/// arrived, and <c>MEASUREMENT_STATUS</c> §55 forbids rendering those two alike.
/// </param>
/// <param name="ModelRuns">Turns that DID reach a model.</param>
/// <param name="RunsWithoutUsage">Model turns whose metrics carried neither token count.</param>
/// <param name="RunsWithPartialUsage">Model turns whose metrics carried exactly one (§60.2).</param>
/// <param name="RunsWithoutCost">Model turns whose metrics carried no estimated cost.</param>
/// <param name="RunsWithoutModelId">Model turns whose metrics named no model.</param>
/// <param name="ModelIds">Every distinct model id the arm's turns named. Empty for a no-model arm.</param>
/// <remarks>
/// The six trailing members are OPTIONAL with a zero/empty default so a snapshot written before
/// 8.3 still deserialises. ⚠ A pre-8.3 document therefore reads back as <c>ModelRuns = 0</c>,
/// i.e. NoModel, for every arm — which is why <see cref="ArmCostSnapshot.StateIsRecorded"/> exists
/// and why the printer reads the LIVE report rather than a rehydrated snapshot.
/// </remarks>
public sealed record ArmCostSnapshot(
    int Runs,
    long DurationMs,
    int PromptTokens,
    int CompletionTokens,
    decimal EstimatedCost,
    int ModelFreeRuns = 0,
    int ModelRuns = 0,
    int RunsWithoutUsage = 0,
    int RunsWithPartialUsage = 0,
    int RunsWithoutCost = 0,
    int RunsWithoutModelId = 0,
    IReadOnlyList<string>? ModelIds = null)
{
    /// <summary>
    /// False for a document written before plan item 8.3, where the run-state members are absent and
    /// their defaults are indistinguishable from a genuine deterministic arm.
    /// </summary>
    /// <remarks>
    /// ⚠ A reader must consult this BEFORE reading the state, for exactly the reason 8.3 exists: a
    /// zero that means "not recorded" and a zero that means "measured as none" are different facts,
    /// and a record that cannot tell them apart must say so rather than pick one.
    /// </remarks>
    public bool StateIsRecorded => ModelFreeRuns + ModelRuns == Runs && Runs > 0;
}

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

/// <summary>
/// One judged-quality cell from Eval 05 — one persona, one arm.
/// </summary>
/// <remarks>
/// ⚠ <b>The weighted score and the instrument flag travel together, and neither is quotable
/// alone.</b> <c>InstrumentFailed</c> is true when the judge returned no verdict for a declared
/// criterion, and the weighted score then contains a zero that is an artefact of the instrument
/// rather than a fact about the answer. A stored score with no flag beside it is exactly how
/// correction ⑫ happened.
/// </remarks>
/// <param name="CaseId">The persona/case.</param>
/// <param name="Arm">"agent" or "popularity".</param>
/// <param name="WeightedScore">0-100 over the DECLARED rubric, never over the criteria that came back.</param>
/// <param name="HolisticScore">The harness's own overall score. Reported for contrast, never used in a gate.</param>
/// <param name="InstrumentFailed">True when a declared criterion came back without a verdict.</param>
/// <param name="Presentations">How many products the arm presented.</param>
/// <param name="EvidenceResolved">How many presentations carried resolvable evidence.</param>
/// <param name="ExtraCriteriaCount">Criteria the judge returned that did not join to the rubric.</param>
/// <param name="CostUsd">Agent-turn cost. The judge call is NOT in this figure.</param>
/// <param name="Error">A harness-level exception, when the turn threw.</param>
public sealed record QualityCellSnapshot(
    string CaseId,
    string Arm,
    double WeightedScore,
    int HolisticScore,
    bool InstrumentFailed,
    int Presentations,
    int EvidenceResolved,
    int ExtraCriteriaCount,
    decimal? CostUsd,
    string? Error);

/// <summary>Serialisable snapshot of one Eval 05 run.</summary>
/// <remarks>
/// <para>
/// <b>Why this exists (plan item 8.20).</b> Eval 05 persisted nothing and said nothing about it,
/// while being the eval whose own re-grade spread on ONE fixed input is <b>25 points</b>
/// (45/30/35/55/35, <c>SUITE_SUMMARY</c> §18.1). A judged number with that much spread is the one
/// number in the suite most in need of a stored baseline, because a single run of it cannot be
/// distinguished from noise without one.
/// </para>
/// <para>
/// ⚠ It is a RECORD, not a gate. Nothing reads it to decide anything, and the margin between the
/// arms it stores is smaller than the spread above — so a later reader comparing two of these files
/// is looking at two draws, not at a change.
/// </para>
/// </remarks>
public sealed record QualitySnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Every cell, agent and control arms alike.</summary>
    public List<QualityCellSnapshot> Cells { get; init; } = [];

    /// <summary>True when the eval's gate passed.</summary>
    public bool GatePassed { get; init; }

    /// <summary>Cells whose judge left a declared criterion unanswered.</summary>
    public int InstrumentFailures { get; init; }

    /// <summary>The judge deployment, so two files scored by two judges are not compared silently.</summary>
    public string JudgeModel { get; init; } = "";

    /// <summary>
    /// The measured re-grade spread on one fixed input, in points, at authoring time.
    /// </summary>
    /// <remarks>
    /// Stored beside the scores on purpose: it is the bound on every number in this file, and a
    /// reader who has the scores without it will over-read a difference smaller than the noise.
    /// </remarks>
    public int JudgeSpreadPoints { get; init; }

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}

/// <summary>One Eval 06 trajectory case.</summary>
/// <param name="CaseId">The case.</param>
/// <param name="Passed">Every authored claim held.</param>
/// <param name="FailedClaims">The claims that did not hold, verbatim.</param>
/// <param name="ToolNames">Tool names in call order — the trajectory itself.</param>
/// <param name="PresentedCount">Products presented.</param>
/// <param name="ApprovalRequests">Commit calls that reached the approval gate.</param>
/// <param name="BudgetUsed">Distinct tool calls charged against the per-turn budget.</param>
/// <param name="BudgetCap">The cap.</param>
/// <param name="BudgetOverrun">True when a tool answered with the budget-exhausted refusal.</param>
/// <param name="CostUsd">Estimated turn cost.</param>
public sealed record TrajectoryCaseSnapshot(
    string CaseId,
    bool Passed,
    IReadOnlyList<string> FailedClaims,
    IReadOnlyList<string> ToolNames,
    int PresentedCount,
    int ApprovalRequests,
    int BudgetUsed,
    int BudgetCap,
    bool BudgetOverrun,
    decimal? CostUsd);

/// <summary>Serialisable snapshot of one Eval 06 run.</summary>
/// <remarks>
/// <b>Why this exists (plan item 8.20).</b> Eval 06 persisted nothing and said nothing about it.
/// The ORDER of tool names is the eval's whole subject and it is not recoverable from any other
/// record in the store — T-02's opt-out violation is visible only as <c>GetInterestMap</c> sitting
/// at position #6 of a trajectory, and without this file that observation exists solely in a
/// console log that <c>.gitignore</c> keeps out of the repository.
/// </remarks>
public sealed record TrajectorySnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>One row per case, in run order.</summary>
    public List<TrajectoryCaseSnapshot> Cases { get; init; } = [];

    /// <summary>True when every case's every claim held.</summary>
    public bool GatePassed { get; init; }

    /// <summary>The agent deployment.</summary>
    public string Model { get; init; } = "";

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}
