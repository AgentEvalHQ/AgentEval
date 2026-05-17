// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>A single hand-labeled calibration entry used to evaluate judge accuracy.</summary>
public sealed record CalibrationEntry(
    string ScenarioId,
    string EvaluatorKey,
    string Input,
    string AgentResponse,
    string ExpectedVerdict,
    double ExpectedScoreMin,
    double ExpectedScoreMax,
    string Rationale);

/// <summary>A named collection of calibration entries for a single agentic category.</summary>
public sealed record CalibrationDataset(string CategoryKey, IReadOnlyList<CalibrationEntry> Entries);

/// <summary>
/// Loads <see cref="CalibrationDataset"/> instances from JSONL streams or
/// embedded assembly resources. Groups entries by category derived from the
/// evaluator key: keys starting with <c>task_</c> or <c>intent_</c> map to
/// <c>system</c>; keys starting with <c>tool_</c> map to <c>process</c>.
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
    /// <param name="categoryKey">Logical key identifying the category (e.g. <c>system</c> or <c>process</c>).</param>
    /// <param name="jsonl">Readable stream containing JSONL content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed <see cref="CalibrationDataset"/>.</returns>
    public async Task<CalibrationDataset> LoadAsync(string categoryKey, Stream jsonl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryKey);
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
                    $"Calibration {categoryKey} line {lineNumber} malformed: {ex.Message}", ex);
            }
        }
        return new CalibrationDataset(categoryKey, entries);
    }

    /// <summary>
    /// Loads all calibration datasets from JSONL files embedded in <paramref name="assembly"/>,
    /// grouped by agentic category (system / process). Only resources whose name contains
    /// <c>.AgenticBenchmark.Golden.</c> or <c>.Agentic.Calibration.Golden.</c>
    /// and ends with <c>.jsonl</c> are considered.
    /// </summary>
    /// <param name="assembly">The assembly that contains the embedded JSONL resources.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All loaded datasets, one per category (system, process).</returns>
    public async Task<IReadOnlyList<CalibrationDataset>> LoadAllFromAssemblyAsync(
        Assembly assembly, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                        (n.Contains(".AgenticBenchmark.Golden.", StringComparison.OrdinalIgnoreCase) ||
                         n.Contains(".Agentic.Calibration.Golden.", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Load every file and accumulate all entries, then group by implied category.
        var allEntries = new List<CalibrationEntry>();
        foreach (var resourceName in resourceNames)
        {
            // Extract category key from the filename segment, e.g. "...golden-20-system.jsonl" → "system"
            var parts = resourceName.Split('.');
            var fileName = parts.Length >= 2 ? parts[^2] : resourceName;
            var categoryKey = fileName.StartsWith("golden-", StringComparison.OrdinalIgnoreCase)
                ? DeriveCategory(fileName[("golden-".Length)..])
                : DeriveCategory(fileName);

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            var dataset = await LoadAsync(categoryKey, stream, ct);
            allEntries.AddRange(dataset.Entries);
        }

        // Group by implied category derived from each entry's evaluator key.
        return allEntries
            .GroupBy(e => DeriveCategory(e.EvaluatorKey))
            .Select(g => new CalibrationDataset(g.Key, g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Derives the agentic category from a file name suffix or evaluator key.
    /// <c>task_*</c> and <c>intent_*</c> → <c>system</c>;
    /// <c>tool_*</c> → <c>process</c>;
    /// numeric suffixes like <c>20-system</c> → <c>system</c>;
    /// unknown keys fall back to <c>unknown</c>.
    /// </summary>
    internal static string DeriveCategory(string keyOrSuffix)
    {
        if (string.IsNullOrWhiteSpace(keyOrSuffix)) return "unknown";

        var lower = keyOrSuffix.ToLowerInvariant();

        // Filename-style suffix: "20-system" or "20-process"
        if (lower.EndsWith("system", StringComparison.Ordinal)) return "system";
        if (lower.EndsWith("process", StringComparison.Ordinal)) return "process";

        // Evaluator key prefix
        if (lower.StartsWith("task_", StringComparison.Ordinal) ||
            lower.StartsWith("intent_", StringComparison.Ordinal))
            return "system";

        if (lower.StartsWith("tool_", StringComparison.Ordinal))
            return "process";

        return "unknown";
    }
}
