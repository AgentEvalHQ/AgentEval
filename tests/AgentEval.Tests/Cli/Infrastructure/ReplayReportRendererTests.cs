// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

public class ReplayReportRendererTests
{
    [Fact]
    public void Render_EmptyReport_StatesNothingToReplay()
    {
        var report = new ReplayReport(Array.Empty<ReplayRow>(), SkippedNonResponseCount: 0);

        var rendered = ReplayReportRenderer.Render(report, "captured.jsonl", "endpoint (model)");

        Assert.Contains("nothing to replay", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_IncludesCapturedPathAndTarget()
    {
        var report = new ReplayReport(Array.Empty<ReplayRow>(), 0);

        var rendered = ReplayReportRenderer.Render(report, "my-capture.jsonl", "https://example.test (gpt-x)");

        Assert.Contains("my-capture.jsonl", rendered, StringComparison.Ordinal);
        Assert.Contains("https://example.test (gpt-x)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_SummarizesPassFlagFailCounts()
    {
        var rows = new[]
        {
            new ReplayRow(0, ReplayVerdict.Pass, "matched", Array.Empty<string>(), 10, 12),
            new ReplayRow(1, ReplayVerdict.Flag, "shape diverged", Array.Empty<string>(), 10, 12),
            new ReplayRow(2, ReplayVerdict.Fail, "tools diverged", Array.Empty<string>(), 10, 12),
        };
        var report = new ReplayReport(rows, SkippedNonResponseCount: 1);

        var rendered = ReplayReportRenderer.Render(report, "c.jsonl", "t");

        Assert.Contains("3 replayed", rendered, StringComparison.Ordinal);
        Assert.Contains("1 pass", rendered, StringComparison.Ordinal);
        Assert.Contains("1 flag", rendered, StringComparison.Ordinal);
        Assert.Contains("1 fail", rendered, StringComparison.Ordinal);
        Assert.Contains("1 skipped", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_EscapesPipesInSummary_DoesNotBreakMarkdownTable()
    {
        var rows = new[] { new ReplayRow(0, ReplayVerdict.Pass, "a | b", Array.Empty<string>(), 1, 1) };
        var report = new ReplayReport(rows, 0);

        var rendered = ReplayReportRenderer.Render(report, "c.jsonl", "t");

        Assert.Contains("a \\| b", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_IncludesEachRowsDetailsInDetailsSection()
    {
        var rows = new[]
        {
            new ReplayRow(0, ReplayVerdict.Fail, "diverged", new[] { "Tool calls differ: captured=[a()] actual=[b()]" }, 5, 6),
        };
        var report = new ReplayReport(rows, 0);

        var rendered = ReplayReportRenderer.Render(report, "c.jsonl", "t");

        Assert.Contains("Tool calls differ: captured=[a()] actual=[b()]", rendered, StringComparison.Ordinal);
    }
}
