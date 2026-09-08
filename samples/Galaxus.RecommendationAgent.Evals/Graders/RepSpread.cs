// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// What one arm's REPETITIONS did on one persona, before they were averaged into the single
/// observation the pairing uses. Design §8.1 row 19 / <b>B-18</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is worth printing.</b> <c>CoverageScore.Mean</c> collapses reps into one number per
/// case, correctly — the unit of analysis is the case, and treating three reps of twelve personas
/// as thirty-six observations is pseudo-replication. But the collapse throws away the ONE thing
/// that says how much of a paired delta is signal: <b>how far apart the same arm's own answers
/// were on the same question.</b>
/// </para>
/// <para>
/// ⚠ <b>An SD over three numbers is itself a very noisy quantity, and this record does not pretend
/// otherwise.</b> Three reps give two degrees of freedom; the sample SD's own relative standard
/// error at n = 3 is about 52 %. That is why <see cref="Range"/> is reported beside it and why
/// <see cref="ReadableAsSpread"/> exists — a "spread" over one run is not a small spread, it is no
/// spread at all, and printing 0.000 for it would be the same class of claim as printing ¤0.0000
/// for an unmeasured cost.
/// </para>
/// </remarks>
/// <param name="PersonaId">The customer.</param>
/// <param name="Arm">The arm label.</param>
/// <param name="Reps">How many repetitions were scored. 1 for a deterministic arm.</param>
/// <param name="Min">Lowest value across reps, NaN when nothing was scorable.</param>
/// <param name="Max">Highest value across reps, NaN when nothing was scorable.</param>
/// <param name="Mean">The value the pairing actually uses.</param>
/// <param name="Sd">
/// SAMPLE standard deviation (n − 1 denominator), NaN when <paramref name="Reps"/> &lt; 2. The
/// population form would report 0.000 for a single observation, which reads as "no variation
/// observed" when the truth is "variation was never observable".
/// </param>
public readonly record struct RepSpread(
    string PersonaId,
    string Arm,
    int Reps,
    double Min,
    double Max,
    double Mean,
    double Sd)
{
    /// <summary>Max − min, NaN when the spread is not readable.</summary>
    public double Range => ReadableAsSpread ? Max - Min : double.NaN;

    /// <summary>
    /// True only when more than one repetition was scored. <b>A single-run arm has no spread, and
    /// that is a different fact from a spread of zero.</b>
    /// </summary>
    public bool ReadableAsSpread => Reps >= 2 && !double.IsNaN(Min) && !double.IsNaN(Max);

    /// <summary>How this row reads, in one word, for a printer that must not invent a number.</summary>
    public string Verdict =>
        Reps == 0 ? "NO REPS SCORED"
        : !ReadableAsSpread ? "NOT REPEATED"
        : Range == 0 ? "identical across reps"
        : "varies";

    /// <summary>Computes the spread of one channel over one cell's repetitions.</summary>
    /// <param name="personaId">The customer.</param>
    /// <param name="arm">The arm label.</param>
    /// <param name="values">One value per repetition, in rep order. NaN entries are DROPPED.</param>
    /// <returns>The spread.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    public static RepSpread Of(string personaId, string arm, IReadOnlyList<double> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);
        ArgumentNullException.ThrowIfNull(values);

        // An unscorable rep is an ABSENCE, not a zero — the same rule the mean follows. Dropping
        // it lowers `Reps`, which is what makes the row say the spread is not readable.
        double[] usable = [.. values.Where(v => !double.IsNaN(v))];

        if (usable.Length == 0)
            return new RepSpread(personaId, arm, 0, double.NaN, double.NaN, double.NaN, double.NaN);

        double mean = usable.Average();
        double sd = usable.Length < 2
            ? double.NaN
            : Math.Sqrt(usable.Sum(v => (v - mean) * (v - mean)) / (usable.Length - 1));

        return new RepSpread(personaId, arm, usable.Length, usable.Min(), usable.Max(), mean, sd);
    }

    /// <summary>The row as one line, with nothing invented where nothing was measured.</summary>
    public string Describe() =>
        ReadableAsSpread
            ? string.Create(CultureInfo.InvariantCulture,
                $"{Arm} · {PersonaId}: {Reps} reps · min {Min:F3} · max {Max:F3} · mean {Mean:F3} · range {Range:F3} · sd {Sd:F3}")
            : $"{Arm} · {PersonaId}: {Verdict} ({Reps} scorable rep(s)) — no spread is claimed, and 0.000 would be a claim.";
}

/// <summary>
/// Every cell's <see cref="RepSpread"/> for one arm, plus the one comparison that makes them worth
/// printing: <b>is the paired delta this suite reports bigger than the arm's own rep-to-rep noise?</b>
/// </summary>
/// <remarks>
/// <para>
/// This is the point of B-18 and the reason it was worth building rather than filing. Eval 02's
/// headline comparisons are differences of a few hundredths. If the live arm's own answers to the
/// SAME question move by more than that between repetitions, the difference is inside the
/// instrument's noise and the direction it points is not a finding.
/// </para>
/// <para>
/// ⚠ <b>It is a BOUND, not a test.</b> Naming a delta "inside the noise" is a statement about
/// magnitude, and the sign test's p-value is still the thing that decides significance. What this
/// adds is the case where p is unremarkable AND the delta is smaller than the arm's own spread —
/// two independent reasons not to read the direction, rather than one.
/// </para>
/// </remarks>
public sealed class RepSpreadReport
{
    private readonly List<RepSpread> _rows = [];

    /// <summary>Every recorded row, in record order.</summary>
    public IReadOnlyList<RepSpread> Rows => _rows;

    /// <summary>Which channel these spreads are of — printed so two panels cannot be confused.</summary>
    public string Channel { get; }

    /// <summary>Creates a report for one channel.</summary>
    /// <param name="channel">"latent coverage" or "precision@k".</param>
    public RepSpreadReport(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        Channel = channel;
    }

    /// <summary>Records one cell's spread.</summary>
    /// <param name="spread">The spread.</param>
    public void Record(RepSpread spread) => _rows.Add(spread);

    /// <summary>
    /// The largest number of SCORABLE reps any recorded cell had. 0 when nothing was recorded.
    /// </summary>
    /// <remarks>
    /// Read rather than passed in, so a panel describing a persisted run cannot be labelled with
    /// the CURRENT run's rep count. That mismatch is the shape this repository has corrected in
    /// three documents: a caveat carrying a number from a different run.
    /// </remarks>
    public int MaxRepsRecorded => _rows.Count == 0 ? 0 : _rows.Max(r => r.Reps);

    /// <summary>Arms recorded, in first-seen order.</summary>
    public IReadOnlyList<string> Arms
    {
        get
        {
            var seen = new List<string>();
            foreach (var row in _rows)
                if (!seen.Contains(row.Arm, StringComparer.Ordinal)) seen.Add(row.Arm);
            return seen;
        }
    }

    /// <summary>Every row for one arm.</summary>
    /// <param name="arm">The arm label.</param>
    public IReadOnlyList<RepSpread> RowsFor(string arm) =>
        [.. _rows.Where(r => string.Equals(r.Arm, arm, StringComparison.Ordinal))];

    /// <summary>The rows for one arm that carry a readable spread.</summary>
    /// <param name="arm">The arm label.</param>
    public IReadOnlyList<RepSpread> ReadableRowsFor(string arm) =>
        [.. RowsFor(arm).Where(r => r.ReadableAsSpread)];

    /// <summary>
    /// One arm's spread summarised over its cells: how many cells were readable, the widest range,
    /// the median range, and the mean sample SD.
    /// </summary>
    /// <param name="arm">The arm label.</param>
    /// <returns>The summary. Every statistic is NaN when no cell was readable.</returns>
    public ArmSpreadSummary SummaryFor(string arm)
    {
        var readable = ReadableRowsFor(arm);
        int cells = RowsFor(arm).Count;

        if (readable.Count == 0)
            return new ArmSpreadSummary(arm, cells, 0, double.NaN, double.NaN, double.NaN, 0);

        double[] ranges = [.. readable.Select(r => r.Range).Order()];
        double median = ranges.Length % 2 == 1
            ? ranges[ranges.Length / 2]
            : (ranges[(ranges.Length / 2) - 1] + ranges[ranges.Length / 2]) / 2.0;

        double[] sds = [.. readable.Select(r => r.Sd).Where(v => !double.IsNaN(v))];

        return new ArmSpreadSummary(
            arm,
            cells,
            readable.Count,
            ranges[^1],
            median,
            sds.Length == 0 ? double.NaN : sds.Average(),
            readable.Count(r => r.Range > 0));
    }

    /// <summary>
    /// Compares a reported paired delta against an arm's own rep-to-rep spread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bound is the WIDEST range observed, not the median, and that choice is the
    /// conservative one.</b> Measured on this corpus's dry run: the live arm's answers were
    /// identical across reps on 11 of 12 cells and moved by 0.250 on the twelfth, so the MEDIAN
    /// range is 0.000. A median of zero certifies every non-zero delta as "outside the noise" —
    /// which is the flattering direction, and it is flattering because most cells being stable says
    /// nothing about the cells that are not. The widest observed movement is what an unlucky cell
    /// can do.
    /// </para>
    /// <para>
    /// ⚠ <b>Two different absences, kept apart.</b> A missing spread and a missing delta are not the
    /// same finding, and one message for both is the defect this suite keeps re-finding. The result
    /// says which side is missing.
    /// </para>
    /// </remarks>
    /// <param name="arm">The repeated arm whose noise is the bound.</param>
    /// <param name="meanDelta">The paired mean delta being read.</param>
    /// <returns>The comparison, or the reason it could not be made.</returns>
    public NoiseComparison CompareToOwnNoise(string arm, double meanDelta)
    {
        var summary = SummaryFor(arm);
        if (!summary.IsReadable || double.IsNaN(summary.WidestRange))
            return new NoiseComparison(NoiseVerdict.NoSpreadRecorded, double.NaN, double.NaN);
        if (double.IsNaN(meanDelta))
            return new NoiseComparison(NoiseVerdict.NoDelta, double.NaN, summary.WidestRange);

        double magnitude = Math.Abs(meanDelta);
        var readable = ReadableRowsFor(arm);
        return new NoiseComparison(
            magnitude > summary.WidestRange ? NoiseVerdict.OutsideNoise : NoiseVerdict.InsideNoise,
            magnitude,
            summary.WidestRange,
            readable.Count(r => r.Range > magnitude),
            readable.Count);
    }
}

/// <summary>How a paired delta sits against the repeated arm's own rep-to-rep spread.</summary>
public enum NoiseVerdict
{
    /// <summary>The arm has no readable spread — it ran once, so it bounds nothing.</summary>
    NoSpreadRecorded,

    /// <summary>The comparison produced no delta to bound (no comparable pair).</summary>
    NoDelta,

    /// <summary>|delta| is no larger than the widest movement the arm showed on its own reps.</summary>
    InsideNoise,

    /// <summary>|delta| exceeds the widest movement the arm showed on its own reps.</summary>
    OutsideNoise,
}

/// <summary>One delta measured against one arm's own noise.</summary>
/// <param name="Verdict">What could be said.</param>
/// <param name="Magnitude">|mean delta|, NaN when there was none.</param>
/// <param name="Bound">The widest rep-to-rep range, NaN when no spread was recorded.</param>
/// <param name="CellsMovingMore">
/// How many readable cells' OWN rep-to-rep range exceeds this delta. ⚠ <b>This is the statistic to
/// read, and the reason is that the inside/outside verdict SATURATES.</b> Latent coverage lives in
/// [0, 1], and on the persisted paid run the live agent's widest rep-to-rep movement is exactly
/// 1.000 — one persona went from 0.000 to 1.000 on the same question. Against a bound of 1.000
/// nothing can ever read "outside", so the verdict alone stops discriminating. A COUNT of cells
/// does not saturate: "the arm's own answers moved by more than this delta on 6 of 12 cells" says
/// the same thing and keeps saying it.
/// </param>
/// <param name="CellsReadable">How many cells carried a readable spread at all.</param>
public readonly record struct NoiseComparison(
    NoiseVerdict Verdict,
    double Magnitude,
    double Bound,
    int CellsMovingMore = 0,
    int CellsReadable = 0)
{
    /// <summary>The sentence for this row. Two absences, two different sentences.</summary>
    public string Describe() => Verdict switch
    {
        NoiseVerdict.NoSpreadRecorded =>
            "NO SPREAD RECORDED — the arm ran once, so it bounds nothing",
        NoiseVerdict.NoDelta =>
            "NO DELTA — the comparison produced none, so there is nothing to bound",
        _ => $"the arm's OWN reps moved by more than this on {CellsMovingMore} of {CellsReadable} cell(s)"
           + (Verdict == NoiseVerdict.InsideNoise
                ? "; ⚠ and the delta is inside its widest movement"
                : "; the delta exceeds its widest movement"),
    };
}

/// <summary>One arm's rep-to-rep spread, summarised over its cells.</summary>
/// <param name="Arm">The arm label.</param>
/// <param name="Cells">How many cells the arm has rows for.</param>
/// <param name="ReadableCells">How many of them had two or more scorable reps.</param>
/// <param name="WidestRange">The largest max − min over the readable cells, NaN when none.</param>
/// <param name="MedianRange">The median max − min over the readable cells, NaN when none.</param>
/// <param name="MeanSd">The mean sample SD over the readable cells, NaN when none.</param>
/// <param name="CellsThatMoved">How many readable cells had a range greater than zero.</param>
public readonly record struct ArmSpreadSummary(
    string Arm,
    int Cells,
    int ReadableCells,
    double WidestRange,
    double MedianRange,
    double MeanSd,
    int CellsThatMoved)
{
    /// <summary>True when this arm's spread can bound anything at all.</summary>
    public bool IsReadable => ReadableCells > 0 && !double.IsNaN(MedianRange);

    /// <summary>The summary as one sentence, with nothing invented where nothing was measured.</summary>
    public string Describe() =>
        !IsReadable
            ? $"{Arm}: NOT REPEATED — {Cells} cell(s), none with two or more scorable reps. One run IS this "
            + "arm's whole distribution, so it bounds nothing and no spread is reported for it."
            : string.Create(CultureInfo.InvariantCulture,
                $"{Arm}: {ReadableCells} of {Cells} cell(s) readable · {CellsThatMoved} moved between reps · widest range {WidestRange:F3} · median range {MedianRange:F3} · mean sd {MeanSd:F3}");
}
