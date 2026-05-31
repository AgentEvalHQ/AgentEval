// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core.Reporting;
using Xunit;

namespace AgentEval.Tests.Reporting;

/// <summary>
/// Unit tests for <see cref="MarkdownText"/> — the centralised Markdown sanitisation helper that
/// backs the SEC-16 fix (escaping free-form subject names / run ids before interpolation).
/// </summary>
public class MarkdownTextTests
{
    [Theory]
    [InlineData("a|b", "a\\|b")]
    [InlineData("a*b", "a\\*b")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("a`b", "a\\`b")]
    public void Escape_EscapesControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, MarkdownText.Escape(input));
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownText.Escape(null));
        Assert.Equal(string.Empty, MarkdownText.Escape(""));
    }

    [Fact]
    public void EscapeInline_ReplacesBacktick_SoCodeSpanCannotBreakOut()
    {
        // A backslash escape does not work inside a `code span`; the backtick must be removed.
        Assert.Equal("a'b", MarkdownText.EscapeInline("a`b"));
    }

    [Fact]
    public void EscapeInline_CollapsesNewlines()
    {
        // CR and LF are each replaced with a space (CRLF → two spaces); the key property is that
        // no newline survives to inject a standalone Markdown line.
        var result = MarkdownText.EscapeInline("a\r\nb\nc");
        Assert.DoesNotContain('\n', result);
        Assert.DoesNotContain('\r', result);
        Assert.Equal("a  b c", result);
    }

    [Fact]
    public void EscapeInline_EscapesPipeAndBrackets()
    {
        Assert.Equal("\\[link\\]\\|x", MarkdownText.EscapeInline("[link]|x"));
    }

    [Fact]
    public void EscapeInline_Truncates()
    {
        var result = MarkdownText.EscapeInline(new string('x', 500), maxLength: 10);
        Assert.Equal(new string('x', 10) + "…", result);
    }

    [Fact]
    public void EscapeInline_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkdownText.EscapeInline(null));
        Assert.Equal(string.Empty, MarkdownText.EscapeInline(""));
    }

    [Fact]
    public void Truncate_CapsLengthWithEllipsis()
    {
        Assert.Equal("abcde…", MarkdownText.Truncate("abcdefghij", maxLength: 5));
        Assert.Equal("short", MarkdownText.Truncate("short", maxLength: 10));
    }
}
