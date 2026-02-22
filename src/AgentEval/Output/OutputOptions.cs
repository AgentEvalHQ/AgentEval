// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Configuration options for table output formatting.
/// Controls which columns are displayed in stochastic and comparison tables.
/// </summary>
public class OutputOptions
{
    /// <summary>
    /// Gets the default output options with commonly used columns enabled.
    /// </summary>
    public static OutputOptions Default => new();

    /// <summary>
    /// Gets minimal output options showing only essential columns.
    /// </summary>
    public static OutputOptions Minimal => new()
    {
        ShowTokens = false,
        ShowCost = false,
        ShowTimeToFirstToken = false,
        ShowToolCalls = false,
        ShowMetrics = false
    };

    /// <summary>
    /// Gets full output options showing all available columns.
    /// </summary>
    public static OutputOptions Full => new()
    {
        ShowScore = true,
        ShowPassRate = true,
        ShowDuration = true,
        ShowTimeToFirstToken = true,
        ShowTokens = true,
        ShowPromptTokens = true,
        ShowCompletionTokens = true,
        ShowCost = true,
        ShowToolCalls = true,
        ShowToolSuccess = true,
        ShowMetrics = true,
        ShowConfidenceInterval = true
    };

    /// <summary>
    /// Show the score column (Min/Max/Mean). Default: true.
    /// </summary>
    public bool ShowScore { get; set; } = true;

    /// <summary>
    /// Show pass rate percentage. Default: true.
    /// </summary>
    public bool ShowPassRate { get; set; } = true;

    /// <summary>
    /// Show duration/latency column. Default: true.
    /// </summary>
    public bool ShowDuration { get; set; } = true;

    /// <summary>
    /// Show time to first token (TTFT). Default: true.
    /// </summary>
    public bool ShowTimeToFirstToken { get; set; } = true;

    /// <summary>
    /// Show total token count. Default: true.
    /// </summary>
    public bool ShowTokens { get; set; } = true;

    /// <summary>
    /// Show prompt tokens breakdown. Default: false (opt-in).
    /// </summary>
    public bool ShowPromptTokens { get; set; } = false;

    /// <summary>
    /// Show completion tokens breakdown. Default: false (opt-in).
    /// </summary>
    public bool ShowCompletionTokens { get; set; } = false;

    /// <summary>
    /// Show estimated cost column. Default: true.
    /// </summary>
    public bool ShowCost { get; set; } = true;

    /// <summary>
    /// Show tool call count. Default: true.
    /// </summary>
    public bool ShowToolCalls { get; set; } = true;

    /// <summary>
    /// Show tool success rate. Default: true.
    /// </summary>
    public bool ShowToolSuccess { get; set; } = true;

    /// <summary>
    /// Show evaluation metrics (Faithfulness, Relevance, etc.). Default: true.
    /// </summary>
    public bool ShowMetrics { get; set; } = true;

    /// <summary>
    /// Show confidence intervals for statistics. Default: false (opt-in).
    /// </summary>
    public bool ShowConfidenceInterval { get; set; } = false;

    /// <summary>
    /// The output writer to use. Default: Console.Out.
    /// </summary>
    public TextWriter Writer { get; set; } = Console.Out;

    /// <summary>
    /// Table width in characters. 0 = auto-size. Default: 0.
    /// </summary>
    public int TableWidth { get; set; } = 0;

    /// <summary>
    /// Whether to use colored output (emoji indicators). Default: true.
    /// </summary>
    public bool UseColors { get; set; } = true;

    /// <summary>
    /// Indentation prefix for table rows. Default: "   " (3 spaces).
    /// </summary>
    public string Indent { get; set; } = "   ";

    /// <summary>
    /// Creates a copy of these options with modifications.
    /// </summary>
    public OutputOptions With(Action<OutputOptions> configure)
    {
        var copy = new OutputOptions
        {
            ShowScore = ShowScore,
            ShowPassRate = ShowPassRate,
            ShowDuration = ShowDuration,
            ShowTimeToFirstToken = ShowTimeToFirstToken,
            ShowTokens = ShowTokens,
            ShowPromptTokens = ShowPromptTokens,
            ShowCompletionTokens = ShowCompletionTokens,
            ShowCost = ShowCost,
            ShowToolCalls = ShowToolCalls,
            ShowToolSuccess = ShowToolSuccess,
            ShowMetrics = ShowMetrics,
            ShowConfidenceInterval = ShowConfidenceInterval,
            Writer = Writer,
            TableWidth = TableWidth,
            UseColors = UseColors,
            Indent = Indent
        };
        configure(copy);
        return copy;
    }
}
