// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;

namespace AgentEval.Compliance.Gdpr.Calibration;

/// <summary>A single hand-labeled calibration entry used to evaluate judge accuracy.</summary>
public sealed record CalibrationEntry(
    string ScenarioId,
    string ArticleControlId,
    string Input,
    string AgentResponse,
    string ExpectedVerdict,
    double ExpectedScoreMin,
    double ExpectedScoreMax,
    string Rationale);

/// <summary>A named collection of calibration entries for a single pillar.</summary>
public sealed record CalibrationDataset(string PillarKey, IReadOnlyList<CalibrationEntry> Entries);

/// <summary>
/// Loads <see cref="CalibrationDataset"/> instances from JSONL streams or
/// embedded assembly resources.
/// </summary>
public sealed class CalibrationDatasetLoader
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads a <see cref="CalibrationDataset"/> from a JSONL stream.
    /// Each non-blank line must be a JSON object matching <see cref="CalibrationEntry"/>.
    /// </summary>
    /// <param name="pillarKey">Logical key identifying the pillar (e.g. <c>pillar1-foundations-30</c>).</param>
    /// <param name="jsonl">Readable stream containing JSONL content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="CalibrationDataset"/>.</returns>
    public async Task<CalibrationDataset> LoadAsync(string pillarKey, Stream jsonl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pillarKey);
        ArgumentNullException.ThrowIfNull(jsonl);

        var entries = new List<CalibrationEntry>();
        using var reader = new StreamReader(jsonl);
        string? line;
        int lineNumber = 0;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<CalibrationEntry>(line, s_jsonOpts)
                    ?? throw new InvalidOperationException($"line {lineNumber} parsed to null");
                entries.Add(entry);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Calibration {pillarKey} line {lineNumber} malformed: {ex.Message}", ex);
            }
        }
        return new CalibrationDataset(pillarKey, entries);
    }

    /// <summary>
    /// Loads all GDPR calibration datasets from JSONL files embedded in <paramref name="assembly"/>.
    /// Only resources whose name contains <c>.Compliance.Gdpr.Calibration.Golden.</c> and ends with
    /// <c>.jsonl</c> are considered — this scoping prevents accidental cross-loading when other
    /// regulations (EU AI Act, agentic) ship golden datasets in the same assembly.
    /// </summary>
    /// <param name="assembly">The assembly that contains the embedded JSONL resources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All loaded datasets, one per matching embedded resource.</returns>
    public async Task<IReadOnlyList<CalibrationDataset>> LoadAllFromAssemblyAsync(
        Assembly assembly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                        n.Contains(".Compliance.Gdpr.Calibration.Golden.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var datasets = new List<CalibrationDataset>();
        foreach (var resourceName in resourceNames)
        {
            // Extract pillar key from the filename segment, e.g. "...golden-pillar1-foundations-30.jsonl"
            var parts = resourceName.Split('.');
            var fileName = parts.Length >= 2 ? parts[^2] : resourceName;
            var pillarKey = fileName.StartsWith("golden-", StringComparison.OrdinalIgnoreCase)
                ? fileName[("golden-".Length)..]
                : fileName;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            datasets.Add(await LoadAsync(pillarKey, stream, ct));
        }
        return datasets;
    }
}
