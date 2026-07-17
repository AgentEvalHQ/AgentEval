// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Guardrails;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// File-system-backed <see cref="ICalibrationReportStore"/> — one JSON file per axis, under
/// <c>{rootPath}/calibration/{axis-slug}.json</c>, OVERWRITTEN on each <see cref="SaveAsync"/> (single-pin per
/// axis; see <see cref="ICalibrationReportStore"/> remarks for why this is deliberately not an append-only
/// ledger like <c>JsonFileBaselineStore</c>). Reuses that store's PATTERN (a caller-rooted directory, plain
/// JSON, tolerate-corrupt-file-on-read), not its TYPE — <c>JsonFileBaselineStore</c> is hard-typed to
/// <c>MemoryBaseline</c> and does Memory-specific side effects (manifest.json rebuild, embedded-resource
/// copies) that do not apply here.
/// <para><see cref="CalibrationReport"/>'s constructor is <see langword="internal"/> to this assembly
/// (<c>AgentEval.Core</c>) — this store lives in the SAME assembly/namespace specifically so it can
/// reconstruct a report directly from persisted data via that constructor, rather than needing a public
/// settable-everything shape on the type callers actually consume.</para>
/// <para><b>Experimental:</b> shipped 2026-07-17, no real-world consumer yet — see
/// <see cref="ICalibrationReportStore"/>'s own experimental note.</para>
/// </summary>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed partial class JsonFileCalibrationReportStore : ICalibrationReportStore
{
    private readonly string _rootPath;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Creates a store rooted at <paramref name="rootPath"/> (typically <c>.agenteval/gatekeeper</c>) — reports are written under <c>{rootPath}/calibration/</c>.</summary>
    public JsonFileCalibrationReportStore(string rootPath = ".agenteval/gatekeeper")
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        _rootPath = rootPath;
    }

    private string CalibrationDir => Path.Combine(_rootPath, "calibration");

    private string PathFor(string axis) => Path.Combine(CalibrationDir, $"{Slugify(axis)}.json");

    /// <inheritdoc/>
    public async Task SaveAsync(CalibrationReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(CalibrationDir);
        var dto = CalibrationReportDto.From(report);
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        await File.WriteAllTextAsync(PathFor(report.Axis), json, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CalibrationReport?> LoadLatestAsync(string axis, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(axis);
        var path = PathFor(axis);
        if (!File.Exists(path))
        {
            return null;
        }

        return await TryReadAsync(path, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, CalibrationReport>> LoadLatestAllAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, CalibrationReport>(StringComparer.Ordinal);
        if (!Directory.Exists(CalibrationDir))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(CalibrationDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var report = await TryReadAsync(file, ct).ConfigureAwait(false);
            if (report is not null)
            {
                // Keyed by the report's OWN Axis field (not the filename slug) — the slug is a
                // filesystem-safety transform, the persisted Axis string is the source of truth.
                result[report.Axis] = report;
            }
        }

        return result;
    }

    private static async Task<CalibrationReport?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<CalibrationReportDto>(json, JsonOptions);
            return dto?.ToReport();
        }
        catch (JsonException)
        {
            // Tolerate a corrupt file rather than fail the whole load — same discipline as
            // JsonFileBaselineStore.TryDeserializeBaseline.
            return null;
        }
    }

    internal static string Slugify(string axis)
    {
        if (string.IsNullOrWhiteSpace(axis))
        {
            return "unnamed";
        }

        var slug = SlugifyRegex().Replace(axis.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(slug))
        {
            slug = "unnamed";
        }

        // A short content-hash suffix so two DIFFERENT axis strings that happen to slugify to the same
        // value (e.g. "foo/bar" and "foo-bar" both -> "foo-bar") resolve to DIFFERENT files instead of
        // silently colliding — consequential here specifically because this store is single-pin-per-axis
        // (OVERWRITE, not append): an un-suffixed collision would silently drop one axis's calibration
        // history, not just produce a duplicate/ambiguous filename the way it would in an append-only
        // ledger like JsonFileBaselineStore. Reuses the existing ManifestFingerprint primitive rather
        // than a bespoke hash.
        var hash = ManifestFingerprint.Hash(axis)[..8];
        var truncated = slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
        return $"{truncated}-{hash}";
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugifyRegex();

    // Plain DTO (public settable properties) so System.Text.Json can round-trip it with zero custom
    // converters — CalibrationReport itself stays internal-constructor-only for its real callers.
    private sealed class CalibrationReportDto
    {
        public string Axis { get; set; } = string.Empty;
        public int TruePositives { get; set; }
        public int TrueNegatives { get; set; }
        public int FalsePositives { get; set; }
        public int FalseNegatives { get; set; }
        public double KappaVsGold { get; set; }
        public double? BaselineAccuracy { get; set; }
        public bool? BeatsBaseline { get; set; }
        public bool MeetsThresholds { get; set; }
        public bool PromotionCriteriaConfigured { get; set; }
        public bool SufficientData { get; set; }
        public List<CalibrationCaseResult> Cases { get; set; } = [];
        public DateTimeOffset CapturedAt { get; set; }

        public static CalibrationReportDto From(CalibrationReport r) => new()
        {
            Axis = r.Axis,
            TruePositives = r.TruePositives,
            TrueNegatives = r.TrueNegatives,
            FalsePositives = r.FalsePositives,
            FalseNegatives = r.FalseNegatives,
            KappaVsGold = r.KappaVsGold,
            BaselineAccuracy = r.BaselineAccuracy,
            BeatsBaseline = r.BeatsBaseline,
            MeetsThresholds = r.MeetsThresholds,
            PromotionCriteriaConfigured = r.PromotionCriteriaConfigured,
            SufficientData = r.SufficientData,
            Cases = [.. r.Cases],
            CapturedAt = r.CapturedAt,
        };

        public CalibrationReport ToReport() => new(
            Axis, TruePositives, TrueNegatives, FalsePositives, FalseNegatives, KappaVsGold,
            BaselineAccuracy, BeatsBaseline, MeetsThresholds, PromotionCriteriaConfigured, SufficientData,
            Cases, CapturedAt);
    }
}
