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
/// from this process, with every rep's presented list in hand, so every rep — live and control
/// alike — is cut to ONE budget: the MINIMUM the live arm presented across its reps. From a
/// PERSISTED snapshot only the rep-averaged cell survives — a ROUNDED MEAN k and a mean recall —
/// so the control is cut once, to that rounded k, and the row is marked. The precision of a
/// persisted live cell is not recoverable (no item list) and is printed as not recorded, never as
/// zero.
/// </para>
/// <para>
/// <b>Why the minimum and not the mean.</b> A rounded rep-mean is a budget no rep necessarily had:
/// reps at 5 / 6 / 5 round to 5, but rep 2's sixth item would then be graded at a k it was never
/// cut to. The minimum is the only k every rep can be cut to without padding, recall is monotone
/// in k, and so the choice moves the live arm's own number DOWN or leaves it alone. It never
/// flatters the arm under test. The raw per-rep counts go into the row's note.
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

            // ⚠ ONE budget for the whole row, and it is the MINIMUM the live arm presented.
            //
            // The rep-matched form of this — every rep cut to its OWN count, then averaged —
            // is what shipped, and it CRASHED the 2026-09-05 live run: reps at 5 / 6 / 5 produce
            // three cuts at three different DeclaredK, and CoverageScore.Mean refuses to average
            // those (correctly: they are different quantities, and the guard is the whole point of
            // this eval). The guard is not the defect. Handing it cuts made at different budgets
            // was.
            //
            // The minimum is the only budget every rep can actually be cut to without padding a
            // short rep with items it never presented. Recall is monotone in k, so cutting the
            // longer reps DOWN can only lower the live arm's own number — the re-read errs
            // against the arm under test, never in its favour — and the raw per-rep counts are
            // printed in the note so nothing is hidden by the choice.
            int kRow = nonSilent.Min(r => r.Count);

            var liveCuts = nonSilent
                .Select(r => InterestCoverageGrader.GradeAtDeclaredK(persona, goldByPersona, r, kRow))
                .ToList();

            // No `with { KUniformAcrossReps = true }`. That flag is exactly what
            // SignTestAtEqualK reads to decide a pair is comparable, so asserting it here would be
            // the artifact under test supplying an input to its own pass/fail. Mean COMPUTES it,
            // and after a common cut it computes true because every rep really does carry kRow.
            CoverageScore liveCell = CoverageScore.Mean(liveCuts);

            var controls = new Dictionary<string, CoverageScore?>(StringComparer.Ordinal);
            var shortControls = new List<string>();

            foreach (string arm in deterministicArms)
            {
                var armReps = ownK.PresentedRepsOf(persona, arm);
                if (armReps.Count == 0) { controls[arm] = null; continue; }
                IReadOnlyList<PresentedCall> list = armReps[0];

                // One cut, to the row's one budget. A deterministic arm has one rep, and cutting
                // the same list to the same k three times and averaging is that same cut.
                if (list.Count < kRow) { controls[arm] = null; shortControls.Add(arm); continue; }
                controls[arm] = InterestCoverageGrader.GradeAtDeclaredK(persona, goldByPersona, list, kRow);
            }

            // Every cell on this row was cut to kRow, so DeclaredK and PresentedCount already ARE
            // kRow on both sides. Nothing is overwritten after the fact.
            int kLive = kRow;
            bool uniform = nonSilent.All(r => r.Count == kRow);

            report.Record(persona, liveArm, liveCell);
            foreach (var (arm, cut) in controls)
            {
                if (cut is { } c) report.Record(persona, arm, c);
            }

            string note = string.Join(" ",
                new[]
                {
                    nonSilent.Count < liveReps.Count ? $"{liveReps.Count - nonSilent.Count} of {liveReps.Count} live rep(s) SILENT and excluded." : "",
                    uniform ? "" : $"live reps presented {string.Join("/", nonSilent.Select(r => r.Count))} — EVERY rep cut to k = {kRow}, the minimum, so one budget covers the cell. Cutting down can only lower the live number.",
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
