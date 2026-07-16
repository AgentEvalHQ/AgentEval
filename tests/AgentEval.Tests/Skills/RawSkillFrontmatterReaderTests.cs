// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

public class RawSkillFrontmatterReaderTests
{
    [Fact]
    public void Parse_WellFormedFrontmatter_ExtractsAllThreeFields()
    {
        var lines = new[]
        {
            "---",
            "name: expense-report",
            "description: Summarizes expense reports.",
            "compatibility: works with GPT-4 class models",
            "---",
            "",
            "Body text.",
        };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("expense-report", result.Name);
        Assert.Equal("Summarizes expense reports.", result.Description);
        Assert.Equal("works with GPT-4 class models", result.Compatibility);
    }

    [Fact]
    public void Parse_NoFrontmatterDelimiters_ReturnsAllNull()
    {
        var lines = new[] { "Just a body, no frontmatter block at all." };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Null(result.Name);
        Assert.Null(result.Description);
        Assert.Null(result.Compatibility);
    }

    [Fact]
    public void Parse_MissingNameKey_NameIsNull_OthersPresent()
    {
        var lines = new[] { "---", "description: has no name field.", "---", "Body." };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Null(result.Name);
        Assert.Equal("has no name field.", result.Description);
    }

    [Fact]
    public void Parse_EmptyDescriptionValue_TreatedAsNull()
    {
        var lines = new[] { "---", "name: empty-description", "description: ", "---", "Body." };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("empty-description", result.Name);
        Assert.Null(result.Description);
    }

    [Fact]
    public void Parse_QuotedValues_StripsMatchingQuotes()
    {
        var lines = new[] { "---", "name: \"quoted-name\"", "description: 'single quoted desc'", "---" };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("quoted-name", result.Name);
        Assert.Equal("single quoted desc", result.Description);
    }

    [Fact]
    public void Parse_UnclosedFrontmatterBlock_StillExtractsWhatItSaw()
    {
        // No closing "---" — the reader should not throw, and should still pick up fields it scanned
        // before running off the end of the file.
        var lines = new[] { "---", "name: unclosed-block", "description: never closes." };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("unclosed-block", result.Name);
        Assert.Equal("never closes.", result.Description);
    }

    // ── YAML block/folded scalar handling (review: fixes a real silent mis-parse) ──

    [Theory]
    [InlineData("|")]
    [InlineData("|-")]
    [InlineData("|+")]
    [InlineData(">")]
    [InlineData(">-")]
    [InlineData("|2")]
    [InlineData(">2-")]
    public void Parse_YamlBlockOrFoldedScalarIndicator_DoesNotCaptureTheIndicatorItself(string indicator)
    {
        // Regression guard: a genuine YAML block/folded scalar puts its real multi-line value on the
        // FOLLOWING indented lines, not after the colon — "description: |" previously set Description to
        // the literal string "|" (the bare indicator character), a silently WRONG value rather than an
        // honest "could not recover this field." This line-scan parser has no concept of YAML indentation
        // blocks, so it still can't recover the real multi-line content — but it must not fabricate the
        // indicator itself as if it WERE the content.
        var lines = new[]
        {
            "---",
            $"description: {indicator}",
            "  This is the real multi-line content that a YAML parser would recover.",
            "  A second continuation line.",
            "---",
        };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Null(result.Description);
        Assert.NotEqual(indicator, result.Description);
    }

    [Fact]
    public void Parse_YamlBlockScalar_NameAndCompatibility_AlsoNotCaptured()
    {
        var lines = new[] { "---", "name: |", "  real-name-on-next-line", "compatibility: >", "  folded content", "---" };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Null(result.Name);
        Assert.Null(result.Compatibility);
    }

    [Fact]
    public void Parse_PlainMultiLineValue_WithoutBlockScalarIndicator_OnlyFirstLineRecovered_DocumentedNarrowerLimitation()
    {
        // Distinguishes the FIXED bug (silently capturing a bare "|"/">' indicator as if it were content)
        // from this parser's remaining, narrower, HONEST limitation: a description continued across lines
        // without a YAML block-scalar indicator at all (not idiomatic SKILL.md authoring, but not invalid
        // YAML either) only has its FIRST line recovered — a real but legitimate PARTIAL value, not a wrong
        // one, since "Our return policy allows 30-day returns" is a true (if incomplete) prefix of the
        // actual multi-line description, unlike the "|" case which was pure noise.
        var lines = new[]
        {
            "---",
            "description: Our return policy allows 30-day returns",
            "  with a receipt for electronics purchased after January 1st.",
            "---",
        };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("Our return policy allows 30-day returns", result.Description);
    }

    [Fact]
    public void Parse_LeadingBlankLines_StillFindsOpeningDelimiter()
    {
        var lines = new[] { "", "  ", "---", "name: after-blank-lines", "---" };

        var result = RawSkillFrontmatterReader.Parse(lines);

        Assert.Equal("after-blank-lines", result.Name);
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsAllNull_NeverThrows()
    {
        var result = RawSkillFrontmatterReader.Read(Path.Combine(Path.GetTempPath(), "agenteval-does-not-exist-" + Guid.NewGuid().ToString("N") + ".md"));

        Assert.Null(result.Name);
        Assert.Null(result.Description);
        Assert.Null(result.Compatibility);
    }

    [Fact]
    public void Read_NullOrWhitespacePath_ReturnsAllNull_NeverThrows()
    {
        var result = RawSkillFrontmatterReader.Read(null);
        Assert.Null(result.Name);

        var result2 = RawSkillFrontmatterReader.Read("   ");
        Assert.Null(result2.Name);
    }

    [Fact]
    public async Task Read_RealFile_RoundTripsFrontmatter()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-frontmatter-read-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: real-file-skill\ndescription: read from an actual file.\n---\n\nBody.\n");

            var result = RawSkillFrontmatterReader.Read(path);

            Assert.Equal("real-file-skill", result.Name);
            Assert.Equal("read from an actual file.", result.Description);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
