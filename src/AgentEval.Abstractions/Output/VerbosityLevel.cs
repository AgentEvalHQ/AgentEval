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
    // MNT-06: flow-scoped, not a process-global mutable field. AsyncLocal confines an override to the
    // logical async flow that set it, so it no longer leaks between concurrent evaluations or across
    // parallel unit tests (xUnit runs test classes in parallel), and a child flow's override does not
    // propagate back to its parent. Each flow reads/writes its own value with no lock needed.
    private static readonly AsyncLocal<VerbosityLevel?> _overrideLevel = new();

    /// <summary>
    /// Gets the current verbosity level from configuration.
    /// Priority: Override > Environment Variable > Default (Detailed)
    /// </summary>
    public static VerbosityLevel Current
    {
        get
        {
            if (_overrideLevel.Value.HasValue)
                return _overrideLevel.Value.Value;

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
    /// Temporarily override the verbosity level for the current async flow.
    /// Use in tests or specific scenarios. The override is flow-scoped (AsyncLocal) — it does not leak
    /// to sibling flows, concurrent evaluations, or back to a parent flow (MNT-06).
    /// </summary>
    public static void SetOverride(VerbosityLevel level) => _overrideLevel.Value = level;

    /// <summary>
    /// Clear any verbosity override for the current async flow.
    /// </summary>
    public static void ClearOverride() => _overrideLevel.Value = null;
}
