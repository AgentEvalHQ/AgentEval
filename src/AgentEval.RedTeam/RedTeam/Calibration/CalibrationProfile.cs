// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Relative (z-score) scoring — a native re-implementation of the calibration idea from NVIDIA garak
// (the LLM vulnerability scanner, Apache-2.0, https://github.com/NVIDIA/garak): score a model RELATIVE to a
// reference cohort distribution ("is this model unusually vulnerable vs its peers?") rather than only absolutely.
// We re-implement the concept natively; the reference cohort statistics are user-supplied (we ship no fabricated
// cohort), so a z-score is always honestly relative to a stated, sourced reference.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Calibration;

/// <summary>Reference distribution (per attack) a current scan is calibrated against: mean + standard deviation of
/// the conclusive-resistance score (0–100) across a cohort of models.</summary>
public sealed record CalibrationStat
{
    /// <summary>Reference mean conclusive-resistance score (0–100) for this attack across the cohort.</summary>
    public required double Mean { get; init; }

    /// <summary>Reference standard deviation. <c>0</c> ⇒ z is undefined (no spread to normalize against).</summary>
    public required double StdDev { get; init; }
}

/// <summary>
/// A calibration reference cohort. <see cref="Attacks"/> is keyed by attack name (e.g. <c>"PromptInjection"</c>).
/// <see cref="Source"/> + <see cref="SampleSize"/> record the provenance so a z-score is never read as absolute.
/// We deliberately ship NO built-in cohort — the profile is supplied by the caller (a real measured cohort).
/// </summary>
public sealed record CalibrationProfile
{
    /// <summary>Provenance of the reference cohort (who/what/when it was measured).</summary>
    public required string Source { get; init; }

    /// <summary>Number of models in the reference cohort.</summary>
    public int SampleSize { get; init; }

    /// <summary>Per-attack reference statistics, keyed by attack name (case-insensitive lookup in the calibrator).</summary>
    public required IReadOnlyDictionary<string, CalibrationStat> Attacks { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses a calibration profile from JSON. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static CalibrationProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            var profile = JsonSerializer.Deserialize<CalibrationProfile>(json, Options)
                ?? throw new FormatException("Calibration profile deserialized to null.");
            if (profile.Attacks is null)
                throw new FormatException("Calibration profile has no 'attacks' map.");
            ValidateAttacks(profile.Attacks);
            return profile;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid calibration profile JSON: {ex.Message}", ex);
        }
    }

    // Validates the deserialized cohort up-front so FromJson's documented FormatException contract holds and a
    // malformed cohort can never reach a (possibly long / paid) scan. System.Text.Json enforces neither
    // non-nullable references nor numeric invariants at runtime, so without this:
    //   • a null stat value would NullReferenceException at scan time (Calibrator stat.StdDev),
    //   • a NaN mean would classify as the calm "within normal range" (NaN comparisons are all false),
    //   • a NaN/negative stdDev would render the false "σ=0 — z undefined" interpretation,
    //   • a case-differing duplicate key ("PromptInjection" + "promptinjection") survives deserialization and
    //     then throws ArgumentException (the WRONG type) at the OrdinalIgnoreCase lookup copy in Calibrator.
    private static void ValidateAttacks(IReadOnlyDictionary<string, CalibrationStat> attacks)
    {
        foreach (var (name, stat) in attacks)
        {
            if (stat is null)
                throw new FormatException($"Calibration profile entry '{name}' is null.");
            if (!double.IsFinite(stat.Mean))
                throw new FormatException($"Calibration profile entry '{name}' has a non-finite mean.");
            if (!double.IsFinite(stat.StdDev))
                throw new FormatException($"Calibration profile entry '{name}' has a non-finite stdDev.");
            if (stat.StdDev < 0)
                throw new FormatException($"Calibration profile entry '{name}' has a negative stdDev ({stat.StdDev}).");
        }

        var duplicate = attacks.Keys
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new FormatException($"Calibration profile has case-insensitive duplicate attack key '{duplicate.Key}'.");
    }

    /// <summary>Loads a calibration profile from a JSON file.</summary>
    public static async Task<CalibrationProfile> LoadAsync(string path, CancellationToken cancellationToken = default)
        => FromJson(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
}
