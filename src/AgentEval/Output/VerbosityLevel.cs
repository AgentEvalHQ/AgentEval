// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Controls the amount of detail in test output and trace artifacts.
/// </summary>
public enum VerbosityLevel
{
    /// <summary>
    /// Minimal output - just pass/fail status.
    /// No trace artifacts are saved.
    /// </summary>
    None = 0,

    /// <summary>
    /// Summary statistics - duration, token count, tool count.
    /// Trace artifacts include basic metadata only.
    /// </summary>
    Summary = 1,

    /// <summary>
    /// Detailed output - includes tool timeline and performance breakdown.
    /// Trace artifacts include step-by-step execution data.
    /// This is the default level.
    /// </summary>
    Detailed = 2,

    /// <summary>
    /// Full trace with all arguments, results, conversation history.
    /// Trace artifacts include complete time-travel data.
    /// Use for debugging and regression analysis.
    /// </summary>
    Full = 3
}

/// <summary>
/// Settings for controlling test output verbosity and trace options.
/// </summary>
public class VerbositySettings
{
    /// <summary>
    /// The verbosity level for test output.
    /// </summary>
    public VerbosityLevel Level { get; set; } = VerbosityLevel.Detailed;

    /// <summary>
    /// Whether to include tool arguments in output.
    /// </summary>
    public bool IncludeToolArguments { get; set; } = true;

    /// <summary>
    /// Whether to include tool results in output.
    /// </summary>
    public bool IncludeToolResults { get; set; } = true;

    /// <summary>
    /// Whether to include performance metrics in output.
    /// </summary>
    public bool IncludePerformanceMetrics { get; set; } = true;

    /// <summary>
    /// Whether to include conversation history in output.
    /// Only relevant for VerbosityLevel.Full.
    /// </summary>
    public bool IncludeConversationHistory { get; set; } = false;

    /// <summary>
    /// Whether to save trace files to disk.
    /// </summary>
    public bool SaveTraceFiles { get; set; } = true;

    /// <summary>
    /// Custom directory for trace output files.
    /// If null, uses VerbosityConfiguration.TraceDirectory.
    /// </summary>
    public string? TraceOutputDirectory { get; set; }
}

/// <summary>
/// Configuration for test output verbosity.
/// </summary>
public static class VerbosityConfiguration
{
    private static VerbosityLevel? _overrideLevel;

    /// <summary>
    /// Gets the current verbosity level from configuration.
    /// Priority: Override > Environment Variable > Default (Detailed)
    /// </summary>
    public static VerbosityLevel Current
    {
        get
        {
            if (_overrideLevel.HasValue)
                return _overrideLevel.Value;

            var envVar = Environment.GetEnvironmentVariable("AGENTEVAL_VERBOSITY");
            if (!string.IsNullOrEmpty(envVar) && Enum.TryParse<VerbosityLevel>(envVar, ignoreCase: true, out var level))
                return level;

            return VerbosityLevel.Detailed;
        }
    }

    /// <summary>
    /// Gets whether trace artifacts should be saved.
    /// </summary>
    public static bool SaveTraceArtifacts
    {
        get
        {
            var envVar = Environment.GetEnvironmentVariable("AGENTEVAL_SAVE_TRACES");
            if (!string.IsNullOrEmpty(envVar))
                return envVar.Equals("true", StringComparison.OrdinalIgnoreCase) 
                    || envVar.Equals("1", StringComparison.Ordinal);

            // Default: save traces unless verbosity is None
            return Current != VerbosityLevel.None;
        }
    }

    /// <summary>
    /// Gets the directory for trace artifacts.
    /// </summary>
    public static string TraceDirectory
    {
        get
        {
            var envVar = Environment.GetEnvironmentVariable("AGENTEVAL_TRACE_DIR");
            if (!string.IsNullOrEmpty(envVar))
                return envVar;

            return Path.Combine(Environment.CurrentDirectory, "TestResults", "traces");
        }
    }

    /// <summary>
    /// Temporarily override the verbosity level.
    /// Use in tests or specific scenarios.
    /// </summary>
    public static void SetOverride(VerbosityLevel level) => _overrideLevel = level;

    /// <summary>
    /// Clear any verbosity override.
    /// </summary>
    public static void ClearOverride() => _overrideLevel = null;
}
