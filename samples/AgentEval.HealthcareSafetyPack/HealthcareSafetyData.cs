// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.HealthcareSafetyPack;

/// <summary>A synthetic tool invocation stored alongside a scenario fixture.</summary>
public sealed record ScenarioToolCall(string Name, string? Result, Dictionary<string, string>? Arguments);

/// <summary>
/// A fully synthetic healthcare-support scenario fixture. It carries the case definition the checks
/// need to know what handling is required (urgent red flag? which actions does the environment
/// permit? is dose context complete?) plus a synthetic agent response to grade. No real patient data.
/// </summary>
public sealed record HealthcareScenario(
    string ScenarioId,
    string CheckId,
    string[]? PermittedActions,
    bool Urgent,
    bool MedicationCase,
    bool DoseContextComplete,
    bool ClinicalClaimCase,
    string Input,
    string AgentResponse,
    ScenarioToolCall[]? ToolCalls,
    bool Synthetic,
    bool NotForClinicalUse);

/// <summary>
/// An author-written gold label. The field set mirrors the existing Calibration/Golden/*.jsonl
/// files (GDPR / EU AI Act) so the same downstream tooling conventions apply.
/// </summary>
public sealed record GoldLabel(
    string ScenarioId,
    string ArticleControlId,
    string Input,
    string AgentResponse,
    string ExpectedVerdict,
    double ExpectedScoreMin,
    double ExpectedScoreMax,
    string Rationale);

/// <summary>Loads the synthetic scenario + gold JSONL fixtures. Fully offline.</summary>
public static class HealthcareSafetyData
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Resolves the data directory: the copied-to-output <c>data</c> folder, else the source tree.</summary>
    public static string ResolveDataDirectory()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "data");
        if (File.Exists(Path.Combine(output, "scenarios.jsonl"))) return output;

        // Fallback for an unusual working directory: walk up looking for the sample's data folder.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "AgentEval.HealthcareSafetyPack", "data");
            if (File.Exists(Path.Combine(candidate, "scenarios.jsonl"))) return candidate;
        }

        return output;
    }

    /// <summary>Reads a JSONL file into a list, skipping blank lines and reporting the offending line on error.</summary>
    public static async Task<IReadOnlyList<T>> LoadJsonlAsync<T>(string path, CancellationToken ct = default)
    {
        var items = new List<T>();
        using var reader = new StreamReader(path);
        string? line;
        var lineNumber = 0;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, s_json)
                    ?? throw new InvalidOperationException("line parsed to null");
                items.Add(item);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} line {lineNumber} malformed: {ex.Message}", ex);
            }
        }
        return items;
    }
}
