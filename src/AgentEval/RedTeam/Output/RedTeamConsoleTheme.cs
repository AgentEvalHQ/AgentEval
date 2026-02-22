// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Output;

/// <summary>
/// Color theme for console output with ANSI escape codes.
/// </summary>
public class RedTeamConsoleTheme
{
    private readonly bool _useColors;

    /// <summary>
    /// Creates a new console theme.
    /// </summary>
    /// <param name="useColors">Whether to emit ANSI color codes.</param>
    public RedTeamConsoleTheme(bool useColors = true)
    {
        _useColors = useColors;
    }

    // Severity colors
    /// <summary>Critical severity color (bright red).</summary>
    public string Critical => _useColors ? "\x1b[91m" : "";

    /// <summary>High severity color (red).</summary>
    public string High => _useColors ? "\x1b[31m" : "";

    /// <summary>Medium severity color (yellow).</summary>
    public string Medium => _useColors ? "\x1b[33m" : "";

    /// <summary>Low severity color (green).</summary>
    public string Low => _useColors ? "\x1b[32m" : "";

    /// <summary>Informational color (cyan).</summary>
    public string Info => _useColors ? "\x1b[36m" : "";

    // Status colors
    /// <summary>Success status color (bright green).</summary>
    public string Success => _useColors ? "\x1b[92m" : "";

    /// <summary>Failure status color (bright red).</summary>
    public string Failure => _useColors ? "\x1b[91m" : "";

    /// <summary>Warning status color (bright yellow).</summary>
    public string Warning => _useColors ? "\x1b[93m" : "";

    /// <summary>Pending status color (gray).</summary>
    public string Pending => _useColors ? "\x1b[90m" : "";

    // Text styles
    /// <summary>Bold text style.</summary>
    public string Bold => _useColors ? "\x1b[1m" : "";

    /// <summary>Dim text style.</summary>
    public string Dim => _useColors ? "\x1b[2m" : "";

    /// <summary>Underline text style.</summary>
    public string Underline => _useColors ? "\x1b[4m" : "";

    /// <summary>Reset all formatting.</summary>
    public string Reset => _useColors ? "\x1b[0m" : "";

    /// <summary>
    /// Colorizes text based on severity level.
    /// </summary>
    public string Colorize(string text, Severity severity) => severity switch
    {
        Severity.Critical => $"{Critical}{text}{Reset}",
        Severity.High => $"{High}{text}{Reset}",
        Severity.Medium => $"{Medium}{text}{Reset}",
        Severity.Low => $"{Low}{text}{Reset}",
        _ => text
    };

    /// <summary>
    /// Gets a status icon based on pass/fail state.
    /// </summary>
    public string StatusIcon(bool passed, bool useEmoji) => passed
        ? (useEmoji ? "✅" : $"{Success}PASS{Reset}")
        : (useEmoji ? "❌" : $"{Failure}FAIL{Reset}");

    /// <summary>
    /// Gets an icon for the given resistance rate.
    /// </summary>
    public string RateIcon(double rate, bool useEmoji)
    {
        if (rate >= 0.9) return useEmoji ? "✅" : $"{Success}+{Reset}";
        if (rate >= 0.7) return useEmoji ? "⚠️" : $"{Warning}~{Reset}";
        return useEmoji ? "❌" : $"{Failure}-{Reset}";
    }
}
