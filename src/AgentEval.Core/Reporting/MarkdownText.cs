// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core.Reporting;

/// <summary>
/// Centralised Markdown text-sanitisation helpers. Free-form, potentially agent-controlled values
/// (subject name, run id) must be escaped before being interpolated into a Markdown report so a
/// value containing a pipe, backtick, newline, or brackets cannot break a table/header or smuggle a
/// link when the report is viewed on a PR/web surface (SEC-16).
/// </summary>
public static class MarkdownText
{
    /// <summary>
    /// Escapes the Markdown emphasis/table control characters (<c>| * _ `</c>). Suitable for
    /// table-cell content. Newlines are NOT collapsed — use <see cref="EscapeInline"/> for
    /// free-form identifiers rendered into a single line or code span.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("|", "\\|")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`");
    }

    /// <summary>
    /// Sanitises a free-form identifier for safe inline rendering (including inside a
    /// <c>`code span`</c>): collapses CR/LF to spaces, replaces backticks with apostrophes (a
    /// backslash escape does not work inside a code span, so the backtick must be removed to prevent
    /// span break-out), truncates to <paramref name="maxLength"/>, and escapes pipe / emphasis /
    /// bracket characters so it is also safe in a plain or table context (SEC-16).
    /// </summary>
    public static string EscapeInline(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = text.Replace('\r', ' ').Replace('\n', ' ').Replace('`', '\'');
        if (s.Length > maxLength)
            s = s.Substring(0, maxLength) + "…";

        return s
            .Replace("|", "\\|")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("[", "\\[")
            .Replace("]", "\\]");
    }

    /// <summary>
    /// Truncates free-form text to <paramref name="maxLength"/> (with an ellipsis) for non-Markdown
    /// surfaces (e.g. PDF) where escaping is unnecessary but an unbounded length can break the
    /// layout (SEC-16).
    /// </summary>
    public static string Truncate(string? text, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";
    }
}
