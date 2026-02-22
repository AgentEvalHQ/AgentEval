// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Output;

/// <summary>
/// Configuration for RedTeam output formatting.
/// </summary>
public record RedTeamOutputOptions
{
    /// <summary>Output verbosity level.</summary>
    public VerbosityLevel Verbosity { get; init; } = VerbosityLevel.Summary;

    /// <summary>
    /// Enable ANSI color codes. Auto-detected from NO_COLOR env var.
    /// Set explicitly to override detection.
    /// </summary>
    public bool UseColors { get; init; } = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// <summary>Show actual attack prompts and responses (sensitive content).</summary>
    public bool ShowSensitiveContent { get; init; } = false;

    /// <summary>Maximum width for output. 0 = auto-detect from console.</summary>
    public int MaxWidth { get; init; } = 0;

    /// <summary>Include OWASP/MITRE references in output.</summary>
    public bool ShowSecurityReferences { get; init; } = true;

    /// <summary>Show emoji indicators (🛡️ ✅ ❌ ⚠️).</summary>
    public bool UseEmoji { get; init; } = true;

    /// <summary>Default output options.</summary>
    public static RedTeamOutputOptions Default => new();

    /// <summary>Minimal output for CI/CD (no colors, compact).</summary>
    public static RedTeamOutputOptions CI => new()
    {
        UseColors = false,
        UseEmoji = false,
        Verbosity = VerbosityLevel.Summary,
        ShowSensitiveContent = false
    };

    /// <summary>Full output for debugging.</summary>
    public static RedTeamOutputOptions Debug => new()
    {
        Verbosity = VerbosityLevel.Full,
        ShowSensitiveContent = true,
        ShowSecurityReferences = true
    };
}

/// <summary>
/// Verbosity levels for RedTeam output.
/// </summary>
public enum VerbosityLevel
{
    /// <summary>Only final pass/fail and score.</summary>
    Minimal,

    /// <summary>Attack-level summary with pass rates.</summary>
    Summary,

    /// <summary>Include failed probes with reasons.</summary>
    Detailed,

    /// <summary>Full probe-level output including prompts/responses.</summary>
    Full
}
