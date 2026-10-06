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

    /// <summary>
    /// Whether to copy the embedded <c>archetypes.json</c> alongside the report and name it in
    /// <c>manifest.json</c>. Default: <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The file is not read by the shipped report (<c>report.html</c>) or by any AgentEval code; its
    /// <c>expected_scores</c> are hand-written reference numbers that gate nothing. It defaulted to
    /// <see langword="true"/> until that was found, which wrote an unused file into every report
    /// directory and implied the report used it. Set this to <see langword="true"/> only if your own
    /// tooling reads the file.
    /// </remarks>
    public bool IncludeArchetypes { get; set; }
}
