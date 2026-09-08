// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// Persists the Eval 02b and 02c snapshots beside the others, under
/// <see cref="EvalResultStore.StorageLocation"/>, with the same serialiser settings.
/// </summary>
/// <remarks>
/// A separate class rather than three more methods on <see cref="EvalResultStore"/>, so the two
/// new evals do not edit a file another lane may be editing. Same directory, same NaN handling —
/// an empty denominator is <c>NaN</c> in the file, never 0 and never 1.
/// </remarks>
public static class OfflineSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Snapshot key for Eval 02b.</summary>
    public const string StatedNeedKey = "eval02b_stated_need";

    /// <summary>Snapshot key for Eval 02c.</summary>
    public const string HeldOutKey = "eval02c_held_out";

    /// <summary>Writes one snapshot.</summary>
    /// <typeparam name="T">The snapshot record type.</typeparam>
    /// <param name="key">Storage key.</param>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The path written.</returns>
    public static string Save<T>(string key, T snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Directory.CreateDirectory(EvalResultStore.StorageLocation);
        string path = Path.Combine(EvalResultStore.StorageLocation, $"{key}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOpts));

        // The second of the suite's two write chokepoints. The `--ci --dry-run` banner reports what
        // the run actually wrote, so a store that writes without saying so would put the banner
        // back where it was: printing a claim the run falsifies.
        EvalResultStore.RecordWrite(key);
        return path;
    }
}

/// <summary>One arm's cell for one Eval 02b case.</summary>
/// <param name="CaseId">Case id.</param>
/// <param name="PersonaId">Customer id.</param>
/// <param name="Arm">Arm label.</param>
/// <param name="Precision">Constraint-satisfaction precision, rep-averaged.</param>
/// <param name="Presented">Distinct SKUs presented, rep-averaged.</param>
/// <param name="Satisfied">Satisfying SKUs presented, rep-averaged.</param>
/// <param name="Silent">True when every rep presented nothing.</param>
/// <param name="SlotsCovered">Slots covered, rep-averaged.</param>
/// <param name="Floor">The uniform-draw floor for the case, <c>|S| / N</c>.</param>
public sealed record StatedNeedCellSnapshot(
    string CaseId,
    string PersonaId,
    string Arm,
    double Precision,
    int Presented,
    int Satisfied,
    bool Silent,
    int SlotsCovered,
    double Floor);

/// <summary>Serialisable snapshot of one Eval 02b run.</summary>
public sealed record StatedNeedSnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Arms, in report order.</summary>
    public List<string> Arms { get; init; } = [];

    /// <summary>Applicable cases (a satisfying product exists).</summary>
    public int ApplicableCases { get; init; }

    /// <summary>Cases with an empty satisfying set — NOT APPLICABLE, never scored.</summary>
    public List<string> InapplicableCases { get; init; } = [];

    /// <summary>Mean precision per arm over the applicable cases.</summary>
    public Dictionary<string, double> MeanPrecisionByArm { get; init; } = [];

    /// <summary>Mean analytic floor over the applicable cases.</summary>
    public double MeanFloor { get; init; }

    /// <summary>Whether the live arm was measured at all. False ⇒ its cells are absent, not zero.</summary>
    public bool LiveArmMeasured { get; init; }

    /// <summary>Every cell.</summary>
    public List<StatedNeedCellSnapshot> Cells { get; init; } = [];

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}

/// <summary>One arm's cell for one Eval 02c target.</summary>
/// <param name="PersonaId">Customer id.</param>
/// <param name="HiddenPurchaseId">The hidden line.</param>
/// <param name="TargetSku">The hidden SKU.</param>
/// <param name="Arm">Arm label.</param>
/// <param name="SkuHitAtK">Hit on the SKU within the declared k, rep-averaged.</param>
/// <param name="LeafHitAtK">Hit on the leaf within the declared k, rep-averaged.</param>
/// <param name="PresentedRaw">Distinct SKUs presented before truncation, rep-averaged.</param>
/// <param name="SkuFloor">The SKU floor at k.</param>
/// <param name="LeafFloor">The leaf floor at k.</param>
public sealed record HeldOutCellSnapshot(
    string PersonaId,
    string HiddenPurchaseId,
    string TargetSku,
    string Arm,
    double SkuHitAtK,
    double LeafHitAtK,
    int PresentedRaw,
    double SkuFloor,
    double LeafFloor);

/// <summary>Serialisable snapshot of one Eval 02c run.</summary>
public sealed record HeldOutSnapshot
{
    /// <summary>Human-readable label.</summary>
    public string Label { get; init; } = "";

    /// <summary>The declared budget every arm was cut to.</summary>
    public int K { get; init; }

    /// <summary>Arms, in report order.</summary>
    public List<string> Arms { get; init; } = [];

    /// <summary>Targets scored.</summary>
    public int TargetCount { get; init; }

    /// <summary>Hit-rate@k on the SKU per arm.</summary>
    public Dictionary<string, double> SkuHitRateByArm { get; init; } = [];

    /// <summary>Hit-rate@k on the leaf per arm.</summary>
    public Dictionary<string, double> LeafHitRateByArm { get; init; } = [];

    /// <summary>Mean SKU floor over the targets.</summary>
    public double MeanSkuFloor { get; init; }

    /// <summary>Mean leaf floor over the targets.</summary>
    public double MeanLeafFloor { get; init; }

    /// <summary>Whether the live arm was measured at all. False ⇒ its cells are absent, not zero.</summary>
    public bool LiveArmMeasured { get; init; }

    /// <summary>Every cell.</summary>
    public List<HeldOutCellSnapshot> Cells { get; init; } = [];

    /// <summary>When the run happened.</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;
}
