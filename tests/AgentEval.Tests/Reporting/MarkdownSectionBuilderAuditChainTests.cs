// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core.Reporting;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Reporting;

/// <summary>
/// Pins the <c>## Audit Chain</c> section written by <see cref="MarkdownSectionBuilder.AppendAuditChain"/>
/// (shared by the GDPR and EU AI Act Markdown reports). The helper only sees the hash copied into the
/// evidence, never the source run it points at, so it cannot verify the chain. It used to print
/// <c>VALID</c> for any non-empty hash and <c>BROKEN</c> for an empty one; both states were claims about a
/// check that never ran. These tests fail on that rendering and pass on the honest one.
/// </summary>
public class MarkdownSectionBuilderAuditChainTests
{
    private static string Render(SourceRunRef sourceRun, string? previousEvidenceRef = null)
    {
        var sb = new StringBuilder();
        MarkdownSectionBuilder.AppendAuditChain(sb, sourceRun, previousEvidenceRef);
        return sb.ToString();
    }

    private static string Line(string md, string label) =>
        md.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(label, StringComparison.Ordinal));

    [Fact]
    public void HashRecorded_SaysNotVerified_NeverValid()
    {
        var md = Render(new SourceRunRef("run-001", "sha256:abc123"));

        // The old rendering printed "**Chain status**: **VALID**" here without comparing the hash to anything.
        Assert.DoesNotContain("VALID", md, StringComparison.Ordinal);
        Assert.DoesNotContain("BROKEN", md, StringComparison.Ordinal);
        Assert.Equal("**Chain status**: hash recorded, **not verified** in this report", Line(md, "**Chain status**"));
        Assert.Equal("**Manifest hash**: `sha256:abc123`", Line(md, "**Manifest hash**").TrimEnd());
        Assert.Equal("**Source run**: `run-001`", Line(md, "**Source run**").TrimEnd());
    }

    [Fact]
    public void HashRecorded_NamesTheCommandThatVerifies()
    {
        var md = Render(new SourceRunRef("run-001", "sha256:abc123"));

        // A reader is told how to run the check this report did not run. `agenteval doctor` is the
        // existing command that re-hashes runs and checks compliance evidence against its source run.
        var howTo = Line(md, "To verify");
        Assert.Contains("`agenteval doctor`", howTo, StringComparison.Ordinal);
        Assert.Contains("`contentHash`", howTo, StringComparison.Ordinal);
        Assert.Contains("`manifest.json`", howTo, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NoHash_SaysNoHashRecorded_NotBroken(string? manifestHash)
    {
        var md = Render(new SourceRunRef("run-002", manifestHash!));

        // The old rendering printed "**Chain status**: **BROKEN**", which reads as a failed check.
        // Nothing was checked: there is no hash to check.
        Assert.DoesNotContain("BROKEN", md, StringComparison.Ordinal);
        Assert.DoesNotContain("VALID", md, StringComparison.Ordinal);
        Assert.Equal(
            "**Chain status**: **no hash recorded**, so this evidence cannot be checked against its source run",
            Line(md, "**Chain status**"));
        Assert.Equal("**Manifest hash**: —", Line(md, "**Manifest hash**").TrimEnd());
        // No verification instruction: with no hash there is nothing for the reader to compare.
        Assert.DoesNotContain("agenteval doctor", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Section_HasHeadingAndEndsWithBlankLine()
    {
        var md = Render(new SourceRunRef("run-001", "sha256:abc123"));

        Assert.StartsWith("## Audit Chain", md, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine + Environment.NewLine, md, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousEvidence_RenderedWhenSupplied_DashWhenAbsent()
    {
        Assert.Equal(
            "**Previous evidence**: `prev-evidence-1`",
            Line(Render(new SourceRunRef("run-001", "h"), "prev-evidence-1"), "**Previous evidence**").TrimEnd());
        Assert.Equal(
            "**Previous evidence**: —",
            Line(Render(new SourceRunRef("run-001", "h")), "**Previous evidence**").TrimEnd());
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MarkdownSectionBuilder.AppendAuditChain(null!, new SourceRunRef("r", "h")));
        Assert.Throws<ArgumentNullException>(() =>
            MarkdownSectionBuilder.AppendAuditChain(new StringBuilder(), null!));
    }
}
