// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Graders;

/// <summary>
/// One persona's row of the own-k re-read: the live arm at the k it actually chose, and every
/// deterministic arm CUT to that same k.
/// </summary>
/// <param name="PersonaId">Customer id.</param>
/// <param name="KLive">The live arm's own presentation count for this persona.</param>
/// <param name="KUniform">True when every live rep presented <paramref name="KLive"/> items; false when the count is a rounded mean.</param>
/// <param name="Live">The live cell, at its own k.</param>
/// <param name="ControlsAtKLive">Each deterministic arm re-cut to <paramref name="KLive"/>, or null when it could not be cut that far.</param>
/// <param name="Note">Anything the reader must see beside this row — silence, a rounded k, a short control.</param>
public sealed record OwnKRereadRow(
    string PersonaId,
    int KLive,
    bool KUniform,
    CoverageScore Live,
    IReadOnlyDictionary<string, CoverageScore?> ControlsAtKLive,
    string Note);

/// <summary>
/// Re-reads a coverage comparison at the LIVE arm's own k, persona by persona, by cutting every
/// deterministic arm down to the count the live arm actually presented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The 2026-09-04 live run of Eval 02 cost $18.56 and was paired k-blind:
/// its live arm presented 0–4 items against controls presenting exactly 5, on a recall metric that
/// is monotone in k. The live cells of that run cannot be raised — the agent was never told a
/// budget and the per-rep item lists were not persisted — but the CONTROLS are deterministic and
/// free, so they can be cut to the live arm's k and the pairing made fair at zero cost. That is
/// what this does. It is the only reading of that run that compares like with like.
/// </para>
/// <para>
/// <b>Two sources for the live cells, and the row says which.</b> In a live run the cells come
/// from this process, with every rep's presented list in hand, so the cut is rep-matched: each
/// control is cut to each live rep's own k and the cuts are averaged, exactly as the live reps
/// are. From a PERSISTED snapshot only the rep-averaged cell survives — a ROUNDED MEAN k and a
/// mean recall — so the control is cut once, to that rounded k, and the row is marked. The
/// precision of a persisted live cell is not recoverable (no item list) and is printed as not
/// recorded, never as zero.
/// </para>
/// <para>
/// <b>A silent live cell is not re-read.</b> k = 0 has nothing to cut a control to. The row is
/// kept, the controls are shown at their own k, and the pairing reports it NOT COMPARABLE.
/// </para>
/// </remarks>
public static class OwnKReread
{
    /// <summary>Builds the re-read from the live cells of THIS run.</summary>
    /// <param name="ownK">The own-k report, with every rep's presented list recorded.</param>
    /// <param name="liveArm">The live arm's label.</param>
    /// <param name="deterministicArms">The arms to cut, in report order.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold.</param>
    public static (PairedCoverageReport Report, IReadOnlyList<OwnKRereadRow> Rows, string Provenance) FromThisRun(
        PairedCoverageReport ownK,
        string liveArm,
        IReadOnlyList<string> deterministicArms,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona)
    {
        ArgumentNullException.ThrowIfNull(ownK);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveArm);
        ArgumentNullException.ThrowIfNull(deterministicArms);
        ArgumentNullException.ThrowIfNull(goldByPersona);

        var report = new PairedCoverageReport();
        var rows = new List<OwnKRereadRow>();

        foreach (string persona in ownK.Personas)
        {
            var live = ownK.ScoreOf(persona, liveArm);
            if (live is not { IsScorable: true }) continue;

            var liveReps = ownK.PresentedRepsOf(persona, liveArm);
            var nonSilent = liveReps.Where(r => r.Count > 0).ToList();

            if (nonSilent.Count == 0)
            {
                AddSilentRow(report, rows, ownK, persona, liveArm, deterministicArms, live.Value,
                    "SILENT on every rep — nothing to cut a control to.");
                continue;
            }

            // The live cell at its OWN k: each rep cut to its own count (an identity cut) so the
            // precision@k_r and the floor are carried, then averaged like any other cell.
            var liveCuts = nonSilent
                .Select(r => InterestCoverageGrader.GradeAtDeclaredK(persona, goldByPersona, r, r.Count))
                .ToList();
            CoverageScore liveCell = CoverageScore.Mean(liveCuts) with { KUniformAcrossReps = true };

            var controls = new Dictionary<string, CoverageScore?>(StringComparer.Ordinal);
            var shortControls = new List<string>();

            foreach (string arm in deterministicArms)
            {
                var armReps = ownK.PresentedRepsOf(persona, arm);
                if (armReps.Count == 0) { controls[arm] = null; continue; }
                IReadOnlyList<PresentedCall> list = armReps[0];

                // Rep-matched: the control is cut to EACH live rep's k, and the cuts average
                // exactly as the live reps do. Two reps at k = 4 and one at k = 3 pair against
                // two 4-item cuts and one 3-item cut — never against one rounded-mean cut.
                var cuts = new List<CoverageScore>();
                foreach (var rep in nonSilent)
                {
                    if (list.Count < rep.Count) { cuts.Clear(); break; }
                    cuts.Add(InterestCoverageGrader.GradeAtDeclaredK(persona, goldByPersona, list, rep.Count));
                }

                if (cuts.Count == 0) { controls[arm] = null; shortControls.Add(arm); continue; }
                controls[arm] = CoverageScore.Mean(cuts) with { KUniformAcrossReps = true };
            }

            // The row's k is the rounded rep-mean; DeclaredK on the cells is that same number so
            // the equal-k rule sees one budget on both sides. The cut itself was rep-matched.
            int kLive = liveCell.PresentedCount;
            bool uniform = nonSilent.All(r => r.Count == nonSilent[0].Count);
            liveCell = liveCell with { DeclaredK = kLive };

            report.Record(persona, liveArm, liveCell);
            foreach (var (arm, cut) in controls)
            {
                if (cut is { } c) report.Record(persona, arm, c with { DeclaredK = kLive });
            }

            string note = string.Join(" ",
                new[]
                {
                    nonSilent.Count < liveReps.Count ? $"{liveReps.Count - nonSilent.Count} of {liveReps.Count} live rep(s) SILENT and excluded." : "",
                    uniform ? "" : $"live reps presented {string.Join("/", nonSilent.Select(r => r.Count))} — controls cut rep-by-rep.",
                    shortControls.Count > 0 ? $"presented fewer than k_live: {string.Join(", ", shortControls)}." : "",
                }.Where(s => s.Length > 0));

            rows.Add(new OwnKRereadRow(persona, kLive, uniform, liveCell, controls, note));
        }

        return (report, rows, "live cells from THIS run — every rep's item list in hand, controls cut rep-by-rep");
    }

    /// <summary>
    /// Builds the re-read from a PERSISTED snapshot's live cells and this run's deterministic arms.
    /// </summary>
    /// <param name="ownK">This run's own-k report — supplies the deterministic arms' presented lists.</param>
    /// <param name="snapshot">The persisted run whose live cells are being re-read.</param>
    /// <param name="liveArm">The live arm's label, as recorded in the snapshot.</param>
    /// <param name="deterministicArms">The arms to cut, in report order.</param>
    /// <param name="goldByPersona">Every scored persona's derived gold.</param>
    public static (PairedCoverageReport Report, IReadOnlyList<OwnKRereadRow> Rows, string Provenance) FromSnapshot(
        PairedCoverageReport ownK,
        CoverageSnapshot snapshot,
        string liveArm,
        IReadOnlyList<string> deterministicArms,
        IReadOnlyDictionary<string, GoldInterestMap> goldByPersona)
    {
        ArgumentNullException.ThrowIfNull(ownK);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveArm);
        ArgumentNullException.ThrowIfNull(deterministicArms);
        ArgumentNullException.ThrowIfNull(goldByPersona);

        var report = new PairedCoverageReport();
        var rows = new List<OwnKRereadRow>();

        foreach (string persona in ownK.Personas)
        {
            var cell = snapshot.Cells.FirstOrDefault(c =>
                string.Equals(c.PersonaId, persona, StringComparison.Ordinal)
                && string.Equals(c.Arm, liveArm, StringComparison.Ordinal));
            if (cell is null || cell.Latent < 0) continue;

            // ⚠ Synthesised from what the snapshot holds and nothing more. Relevant count and
            // precision are NOT recoverable from a cell that carries no item list, so they stay
            // undefined — a NaN the printer renders as "n/r", never a 0 it could mistake for a
            // measurement.
            bool hasLists = cell.PresentedSkusByRep is { Count: > 0 };
            int kLive = cell.PresentedCount;

            var live = new CoverageScore(
                Latent: cell.Latent,
                Manifest: cell.Manifest < 0 ? double.NaN : cell.Manifest,
                LatentServed: cell.LatentServed,
                LatentTotal: cell.LatentTotal,
                ManifestServed: 0,
                ManifestTotal: 0,
                PresentedCount: kLive,
                NewCategoryCount: 0,
                PhantomCount: cell.PhantomCount,
                LatentFloor: cell.LatentFloor,
                ForcedChoice: cell.ForcedChoice,
                DeclaredK: kLive,
                PresentedBeforeCut: cell.PresentedBeforeCut < 0 ? kLive : cell.PresentedBeforeCut,
                RelevantCount: cell.RelevantCount,
                PrecisionAtK: cell.PrecisionAtK,      // NaN on a snapshot that never recorded it — printed "n/r"
                PrecisionOfPresented: double.NaN,
                PrecisionFloor: cell.PrecisionFloor,
                KUniformAcrossReps: cell.KUniformAcrossReps);

            if (kLive == 0)
            {
                AddSilentRow(report, rows, ownK, persona, liveArm, deterministicArms, live,
                    "SILENT in the persisted run (k = 0) — nothing to cut a control to.");
                continue;
            }

            var controls = new Dictionary<string, CoverageScore?>(StringComparer.Ordinal);
            var shortControls = new List<string>();

            foreach (string arm in deterministicArms)
            {
                var armReps = ownK.PresentedRepsOf(persona, arm);
                if (armReps.Count == 0) { controls[arm] = null; continue; }
                IReadOnlyList<PresentedCall> list = armReps[0];

                if (list.Count < kLive) { controls[arm] = null; shortControls.Add(arm); continue; }
                controls[arm] = InterestCoverageGrader.GradeAtDeclaredK(persona, goldByPersona, list, kLive);
            }

            report.Record(persona, liveArm, live);
            foreach (var (arm, cut) in controls)
            {
                if (cut is { } c) report.Record(persona, arm, c);
            }

            string note = string.Join(" ",
                new[]
                {
                    hasLists ? "" : "k_live is the snapshot's ROUNDED rep-mean; per-rep k was not persisted.",
                    shortControls.Count > 0 ? $"presented fewer than k_live: {string.Join(", ", shortControls)}." : "",
                }.Where(s => s.Length > 0));

            rows.Add(new OwnKRereadRow(persona, kLive, cell.KUniformAcrossReps && hasLists, live, controls, note));
        }

        string provenance = $"live cells from the PERSISTED snapshot of {snapshot.RunAt:yyyy-MM-dd HH:mm} UTC "
                          + $"(DeclaredK = {snapshot.DeclaredK}{(snapshot.DeclaredK == 0 ? " — the agent was told NO budget" : "")}); "
                          + "controls cut to each persona's recorded k_live in THIS run";

        return (report, rows, provenance);
    }

    private static void AddSilentRow(
        PairedCoverageReport report, List<OwnKRereadRow> rows, PairedCoverageReport ownK,
        string persona, string liveArm, IReadOnlyList<string> deterministicArms, CoverageScore live, string note)
    {
        // The live cell stays at k = 0 and the controls stay at their OWN k — the equal-k rule
        // then lists the pair as NOT COMPARABLE (SILENT), which is the only honest reading.
        report.Record(persona, liveArm, live with { DeclaredK = 0 });

        var controls = new Dictionary<string, CoverageScore?>(StringComparer.Ordinal);
        foreach (string arm in deterministicArms)
        {
            var own = ownK.ScoreOf(persona, arm);
            controls[arm] = own;
            if (own is { } s) report.Record(persona, arm, s with { DeclaredK = 0 });
        }

        rows.Add(new OwnKRereadRow(persona, 0, true, live, controls, note));
    }
}
