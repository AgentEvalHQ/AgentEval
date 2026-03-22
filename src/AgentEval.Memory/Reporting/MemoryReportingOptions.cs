// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Reporting;

/// <summary>
/// Configuration options for the memory benchmark reporting system.
/// </summary>
public class MemoryReportingOptions
{
    /// <summary>
    /// Root path for benchmark output. Use {AgentName} as a placeholder token.
    /// Default: ".agenteval/benchmarks/{AgentName}"
    /// </summary>
    public string OutputPath { get; set; } = ".agenteval/benchmarks/{AgentName}";

    /// <summary>Whether to auto-copy report.html from embedded resources on first baseline save.</summary>
    public bool AutoCopyReportTemplate { get; set; } = true;

    /// <summary>Whether to auto-copy archetypes.json alongside the report.</summary>
    public bool IncludeArchetypes { get; set; } = true;
}
