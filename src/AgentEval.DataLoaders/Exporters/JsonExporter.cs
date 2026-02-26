// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results as JSON.
/// </summary>
public class JsonExporter : IResultExporter
{
    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Json;
    
    /// <inheritdoc />
    public string FileExtension => ".json";
    
    /// <inheritdoc />
    public string ContentType => "application/json";

    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <inheritdoc />
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        var jsonReport = new JsonEvaluationReport
        {
            RunId = report.RunId,
            Name = report.Name,
            StartTime = report.StartTime,
            EndTime = report.EndTime,
            DurationMs = (long)report.Duration.TotalMilliseconds,
            Stats = new JsonStats
            {
                Total = report.TotalTests,
                Passed = report.PassedTests,
                Failed = report.FailedTests,
                Skipped = report.SkippedTests,
                PassRate = report.PassRate
            },
            OverallScore = report.OverallScore,
            Agent = report.Agent != null ? new JsonAgentInfo
            {
                Name = report.Agent.Name,
                Model = report.Agent.Model,
                Version = report.Agent.Version
            } : null,
            Results = report.TestResults.Select(r => new JsonTestResult
            {
                Name = r.Name,
                Category = r.Category,
                Score = r.Score,
                Passed = r.Passed,
                Skipped = r.Skipped,
                DurationMs = r.DurationMs,
                Error = r.Error,
                MetricScores = r.MetricScores.Count > 0 ? r.MetricScores : null
            }).ToList(),
            Metadata = report.Metadata.Count > 0 ? report.Metadata : null
        };

        await JsonSerializer.SerializeAsync(output, jsonReport, s_options, ct);
    }
    
    /// <summary>
    /// Export to a string.
    /// </summary>
    public async Task<string> ExportToStringAsync(EvaluationReport report, CancellationToken ct = default)
    {
        using var stream = new MemoryStream();
        await ExportAsync(report, stream, ct);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}

// Internal JSON serialization models
internal class JsonEvaluationReport
{
    public string RunId { get; set; } = "";
    public string? Name { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public long DurationMs { get; set; }
    public JsonStats Stats { get; set; } = new();
    public double OverallScore { get; set; }
    public JsonAgentInfo? Agent { get; set; }
    public List<JsonTestResult> Results { get; set; } = new();
    public Dictionary<string, string>? Metadata { get; set; }
}

internal class JsonStats
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public double PassRate { get; set; }
}

internal class JsonAgentInfo
{
    public string? Name { get; set; }
    public string? Model { get; set; }
    public string? Version { get; set; }
}

internal class JsonTestResult
{
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public double Score { get; set; }
    public bool Passed { get; set; }
    public bool Skipped { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, double>? MetricScores { get; set; }
}
