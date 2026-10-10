// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Guardrails.Judges;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>One calibration case in the report (text truncated / redacted so gold-set payloads aren't persisted at length).</summary>
internal sealed record CalibrationCaseDto(string Text, bool ShouldBlock, bool Blocked);

/// <summary>JSON projection of a <see cref="CalibrationReport"/> printed by <c>gatekeeper calibrate</c>.</summary>
internal sealed record CalibrationReportDto
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string SchemaVersion { get; init; } = "1.1";
    public string Axis { get; init; } = "";
    public string? SplitLabel { get; init; }
    public int TruePositives { get; init; }
    public int TrueNegatives { get; init; }
    public int FalsePositives { get; init; }
    public int FalseNegatives { get; init; }
    public int Total { get; init; }
    public double DecisiveAccuracy { get; init; }
    public double AccuracyLower { get; init; }
    public double AccuracyUpper { get; init; }
    public int DangerousErrorCount { get; init; }
    public double FalsePositiveRate { get; init; }
    public double FprLower { get; init; }
    public double FprUpper { get; init; }
    public double KappaVsGold { get; init; }
    public double? BaselineAccuracy { get; init; }
    public bool? BeatsBaseline { get; init; }
    public bool MeetsThresholds { get; init; }
    public bool PromotionCriteriaConfigured { get; init; }
    public bool SufficientData { get; init; }
    public bool IsInlineReady { get; init; }
    public IReadOnlyList<CalibrationCaseDto>? Cases { get; init; }

    public static CalibrationReportDto From(CalibrationReport r, bool redactCaseText) => new()
    {
        Axis = r.Axis,
        SplitLabel = r.SplitLabel,
        TruePositives = r.TruePositives,
        TrueNegatives = r.TrueNegatives,
        FalsePositives = r.FalsePositives,
        FalseNegatives = r.FalseNegatives,
        Total = r.Total,
        DecisiveAccuracy = r.DecisiveAccuracy,
        AccuracyLower = r.AccuracyInterval.Lower,
        AccuracyUpper = r.AccuracyInterval.Upper,
        DangerousErrorCount = r.DangerousErrorCount,
        FalsePositiveRate = r.FalsePositiveRate,
        FprLower = r.FprInterval.Lower,
        FprUpper = r.FprInterval.Upper,
        KappaVsGold = r.KappaVsGold,
        BaselineAccuracy = r.BaselineAccuracy,
        BeatsBaseline = r.BeatsBaseline,
        MeetsThresholds = r.MeetsThresholds,
        PromotionCriteriaConfigured = r.PromotionCriteriaConfigured,
        SufficientData = r.SufficientData,
        IsInlineReady = r.IsInlineReady,
        // a gold case can itself contain a secret-shaped span → elide entirely for the redact axes, else truncate.
        Cases = r.Cases.Select(c => new CalibrationCaseDto(
            redactCaseText ? "[redacted]" : Truncate(c.Text, 80), c.ShouldBlock, c.Blocked)).ToList(),
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>
/// A calibration certificate — the CLI's honest source for <c>inlineReady</c>. Written by
/// <c>calibrate --certify</c>, read by <c>inspect</c>. Tied to the exact model (<see cref="ModelFingerprint"/>) and
/// gold set (<see cref="GoldSetHash"/>) it certifies. <b>Unsigned local file</b> — accident-prevention, not a
/// cryptographic barrier (see design §7.3).
/// </summary>
internal sealed record CalibrationCertificate(
    string Axis,
    string ModelFingerprint,
    string GoldSetHash,
    bool IsInlineReady,
    string CertifiedAtUtc,
    CalibrationReportDto Report);
