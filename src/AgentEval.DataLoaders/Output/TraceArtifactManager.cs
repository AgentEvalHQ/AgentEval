// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.Output;

/// <summary>
/// Manages saving and loading of trace artifacts for test debugging.
/// </summary>
public class TraceArtifactManager
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _outputDirectory;

    /// <summary>
    /// Creates a TraceArtifactManager with the default trace directory.
    /// </summary>
    public TraceArtifactManager() : this(VerbosityConfiguration.TraceDirectory)
    {
    }

    /// <summary>
    /// Creates a TraceArtifactManager with a custom output directory.
    /// </summary>
    /// <param name="outputDirectory">Directory for trace files.</param>
    public TraceArtifactManager(string outputDirectory)
    {
        _outputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
    }

    /// <summary>
    /// Gets the output directory for trace files.
    /// </summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>
    /// Saves a test result as a trace artifact.
    /// </summary>
    /// <param name="result">The test result to save.</param>
    /// <returns>The path to the saved trace file.</returns>
    public string SaveTestResult(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        EnsureDirectoryExists();

        var fileName = GenerateFileName(result.TestName, "result");
        var filePath = Path.Combine(_outputDirectory, fileName);

        var traceData = BuildTraceData(result);
        var json = JsonSerializer.Serialize(traceData, s_jsonOptions);
        
        File.WriteAllText(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Saves a TimeTravelTrace as a trace artifact.
    /// </summary>
    /// <param name="trace">The trace to save.</param>
    /// <returns>The path to the saved trace file.</returns>
    public string SaveTrace(TimeTravelTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        EnsureDirectoryExists();

        var fileName = GenerateFileName(trace.Test.TestName, "trace");
        var filePath = Path.Combine(_outputDirectory, fileName);

        var json = JsonSerializer.Serialize(trace, s_jsonOptions);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Saves a test result only if it failed.
    /// </summary>
    /// <param name="result">The test result.</param>
    /// <returns>The path to the saved trace file, or null if test passed.</returns>
    public string? SaveIfFailed(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Passed)
        {
            return null;
        }

        return SaveTestResult(result);
    }

    /// <summary>
    /// Loads a TimeTravelTrace from a file.
    /// </summary>
    /// <param name="filePath">Path to the trace file.</param>
    /// <returns>The deserialized trace.</returns>
    public TimeTravelTrace? LoadTrace(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Trace file not found: {filePath}");
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<TimeTravelTrace>(json, s_jsonOptions);
    }

    /// <summary>
    /// Lists all trace files in the output directory.
    /// </summary>
    /// <returns>Paths to all trace files.</returns>
    public IEnumerable<string> ListTraceFiles()
    {
        if (!Directory.Exists(_outputDirectory))
        {
            return [];
        }

        return Directory.GetFiles(_outputDirectory, "*.json")
            .OrderByDescending(f => File.GetCreationTimeUtc(f));
    }

    /// <summary>
    /// Cleans up trace files older than the specified age.
    /// </summary>
    /// <param name="maxAge">Maximum age of files to keep.</param>
    /// <returns>Number of files deleted.</returns>
    public int CleanupOldTraces(TimeSpan maxAge)
    {
        if (!Directory.Exists(_outputDirectory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var filesToDelete = Directory.GetFiles(_outputDirectory, "*.json")
            .Where(f => File.GetCreationTimeUtc(f) < cutoff)
            .ToList();

        foreach (var file in filesToDelete)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // File in use, skip
            }
        }

        return filesToDelete.Count;
    }

    /// <summary>
    /// Gets the most recent trace file for a test name.
    /// </summary>
    /// <param name="testName">Name of the test.</param>
    /// <returns>Path to the most recent trace file, or null if none found.</returns>
    public string? GetMostRecentTrace(string testName)
    {
        if (!Directory.Exists(_outputDirectory))
        {
            return null;
        }

        var sanitized = SanitizeFileName(testName);
        return Directory.GetFiles(_outputDirectory, $"{sanitized}_*.json")
            .OrderByDescending(f => File.GetCreationTimeUtc(f))
            .FirstOrDefault();
    }

    /// <summary>
    /// Saves raw JSON data as a trace artifact.
    /// </summary>
    /// <param name="name">Name for the trace file.</param>
    /// <param name="data">The data object to serialize.</param>
    /// <returns>The path to the saved file.</returns>
    public string SaveRawData(string name, object data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        EnsureDirectoryExists();

        var fileName = GenerateFileName(name, "data");
        var filePath = Path.Combine(_outputDirectory, fileName);

        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_outputDirectory))
        {
            Directory.CreateDirectory(_outputDirectory);
        }
    }

    private static string GenerateFileName(string baseName, string suffix)
    {
        var sanitized = SanitizeFileName(baseName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        return $"{sanitized}_{timestamp}_{suffix}.json";
    }

    private static string SanitizeFileName(string name)
    {
        // Use cross-platform invalid characters (Windows is most restrictive)
        // This ensures consistent filenames across all platforms
        var crossPlatformInvalid = new HashSet<char>(Path.GetInvalidFileNameChars())
        {
            ':', '*', '?', '"', '<', '>', '|', '/', '\\'
        };
        
        var sanitized = new string(name
            .Replace(' ', '_')
            .Replace('.', '_')
            .Where(c => !crossPlatformInvalid.Contains(c))
            .ToArray());
        
        // Limit length to avoid path issues
        if (sanitized.Length > 100)
        {
            sanitized = sanitized[..100];
        }
        
        return sanitized;
    }

    private static object BuildTraceData(TestResult result)
    {
        return new
        {
            schemaVersion = "1.0",
            testName = result.TestName,
            passed = result.Passed,
            score = result.Score,
            timestamp = DateTimeOffset.UtcNow,
            actualOutput = result.ActualOutput,
            details = result.Details,
            performance = result.Performance is null ? null : new
            {
                totalDurationMs = result.Performance.TotalDuration.TotalMilliseconds,
                timeToFirstTokenMs = result.Performance.TimeToFirstToken?.TotalMilliseconds,
                promptTokens = result.Performance.PromptTokens,
                completionTokens = result.Performance.CompletionTokens,
                estimatedCost = result.Performance.EstimatedCost,
                modelUsed = result.Performance.ModelUsed,
                wasStreaming = result.Performance.WasStreaming
            },
            toolUsage = result.ToolUsage is null ? null : new
            {
                totalCalls = result.ToolUsage.Count,
                hasErrors = result.ToolUsage.HasErrors,
                totalToolTimeMs = result.ToolUsage.TotalToolTime.TotalMilliseconds,
                calls = result.ToolUsage.Calls.Select(c => new
                {
                    name = c.Name,
                    order = c.Order,
                    callId = c.CallId,
                    arguments = c.Arguments,
                    result = c.Result,
                    durationMs = c.Duration?.TotalMilliseconds,
                    hasError = c.HasError,
                    error = c.Exception?.Message
                })
            },
            metrics = result.MetricResults?.Select(m => new
            {
                name = m.MetricName,
                score = m.Score,
                passed = m.Passed,
                explanation = m.Explanation
            }),
            failure = result.Failure is null ? null : new
            {
                whyItFailed = result.Failure.WhyItFailed,
                reasons = result.Failure.Reasons.Select(r => r.Description),
                suggestions = result.Failure.Suggestions.Select(s => s.Title)
            },
            timeline = result.Timeline,
            error = result.Error?.Message,
            errorStackTrace = result.Error?.StackTrace
        };
    }
}
