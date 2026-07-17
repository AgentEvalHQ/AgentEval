// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Gatekeeper next-wave item — the prerequisite the Fleet Health Index needs: <see cref="CalibrationReport"/>
/// is, today, purely an in-memory return value of <see cref="GateCalibrationHarness.EvaluateAsync"/> — nothing
/// persists it. This is a small, deliberately minimal persistence seam: ONE report per axis (the most recent
/// calibration run), not a full historical ledger — <see cref="GatekeeperFleetHealthIndex"/> only ever needs
/// "what does this axis look like right now," not a time series. (A full multi-snapshot history ledger is a
/// separate, larger feature — see the Agent Skills baseline ledger for that shape, deliberately NOT
/// duplicated here.)
/// <para><b>Experimental:</b> shipped 2026-07-17, no real-world consumer yet — see
/// <see cref="GatekeeperFleetHealthIndex"/>'s own experimental note.</para>
/// </summary>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public interface ICalibrationReportStore
{
    /// <summary>
    /// Saves <paramref name="report"/> as the current snapshot for its <see cref="CalibrationReport.Axis"/> —
    /// OVERWRITES any prior snapshot for that same axis (single-pin per axis, not an append-only ledger).
    /// </summary>
    Task SaveAsync(CalibrationReport report, CancellationToken ct = default);

    /// <summary>The most recently saved report for <paramref name="axis"/>, or <see langword="null"/> if that axis has never been calibrated (and saved).</summary>
    Task<CalibrationReport?> LoadLatestAsync(string axis, CancellationToken ct = default);

    /// <summary>Every axis that has ever been saved, mapped to its current (most recent) report.</summary>
    Task<IReadOnlyDictionary<string, CalibrationReport>> LoadLatestAllAsync(CancellationToken ct = default);
}
