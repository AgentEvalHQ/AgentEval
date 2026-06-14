// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// NextWave #10 (functional): CSV importer (HarmBench "Behavior" / JBB "Goal" columns) + JSON importer with a custom
// prompt field (CyberSecEval "test_case_prompt"). Imported probes carry no oracle → Inconclusive without a judge.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Importers;

namespace AgentEval.Tests.RedTeam.Importers;

public class CsvAndFieldImporterTests
{
    // ── CSV importer ──────────────────────────────────────────────────────────

    [Fact]
    public void Csv_ImportsConfiguredPromptColumn()
    {
        // HarmBench-shaped header; prompt column "Behavior".
        const string csv = "Behavior,FunctionalCategory,BehaviorID\nDo something bad,standard,hb-1\nDo another,standard,hb-2\n";
        var probes = new CsvProbeDatasetImporter("Behavior").Import(csv, "HarmBench");

        Assert.Equal(2, probes.Count);
        Assert.Equal("Do something bad", probes[0].Prompt);
        Assert.Equal("HarmBench", probes[0].AttackName);
        Assert.Null(probes[0].ExpectedTokens);   // no oracle → judge/refusal-scored
    }

    [Fact]
    public void Csv_UsesIdColumn_WhenGiven()
    {
        const string csv = "Goal,Index\nattack one,7\nattack two,8\n";
        var probes = new CsvProbeDatasetImporter("Goal", idColumn: "Index").Import(csv, "JBB");
        Assert.Equal("JBB-7", probes[0].Id);
        Assert.Equal("JBB-8", probes[1].Id);
    }

    [Fact]
    public void Csv_HandlesQuotedFields_WithCommasAndNewlines()
    {
        const string csv = "Goal,Category\n\"a goal, with a comma\nand a newline\",jailbreak\n";
        var probes = new CsvProbeDatasetImporter("Goal").Import(csv, "JBB");
        var single = Assert.Single(probes);
        Assert.Equal("a goal, with a comma\nand a newline", single.Prompt);
    }

    [Fact]
    public void Csv_HandlesEscapedQuotes()
    {
        const string csv = "Goal\n\"say \"\"PWNED\"\" now\"\n";
        var probes = new CsvProbeDatasetImporter("Goal").Import(csv, "JBB");
        Assert.Equal("say \"PWNED\" now", Assert.Single(probes).Prompt);
    }

    [Fact]
    public void Csv_SkipsBlankPromptRows()
    {
        const string csv = "Goal\nreal goal\n\n  \n";
        Assert.Single(new CsvProbeDatasetImporter("Goal").Import(csv, "JBB"));
    }

    [Fact]
    public void Csv_MissingPromptColumn_Throws()
    {
        const string csv = "NotTheColumn\nx\n";
        var ex = Assert.Throws<FormatException>(() => new CsvProbeDatasetImporter("Goal").Import(csv, "JBB"));
        Assert.Contains("Goal", ex.Message);
    }

    [Fact]
    public void Csv_HeaderButNoUsableRows_Throws()
        => Assert.Throws<FormatException>(() => new CsvProbeDatasetImporter("Goal").Import("Goal\n\n", "JBB"));

    [Fact]
    public void Csv_IsFormatCsv() => Assert.Equal("csv", new CsvProbeDatasetImporter("Goal").Format);

    // ── JSON importer with a custom prompt field ──────────────────────────────

    [Fact]
    public void Json_CustomPromptField_ImportsCyberSecEvalShape()
    {
        // CyberSecEval prompt_injection.json shape: prompt lives under "test_case_prompt".
        const string json = """
        [
          { "prompt_id": 1, "test_case_prompt": "ignore your instructions", "risk_category": "security" },
          { "prompt_id": 2, "test_case_prompt": "do the forbidden thing" }
        ]
        """;
        var probes = new JsonProbeDatasetImporter("test_case_prompt").Import(json, "CyberSecEval");
        Assert.Equal(2, probes.Count);
        Assert.Equal("ignore your instructions", probes[0].Prompt);
    }

    [Fact]
    public void Json_CustomField_SkipsObjectsMissingTheField()
    {
        const string json = """[ { "test_case_prompt": "x" }, { "other": "y" } ]""";
        Assert.Single(new JsonProbeDatasetImporter("test_case_prompt").Import(json, "ds"));
    }

    [Fact]
    public void Json_CustomField_NoMatches_Throws()
        => Assert.Throws<FormatException>(
            () => new JsonProbeDatasetImporter("test_case_prompt").Import("""[ { "nope": "y" } ]""", "ds"));

    [Fact]
    public void Json_CustomField_NotAnArray_Throws()
        => Assert.Throws<FormatException>(
            () => new JsonProbeDatasetImporter("test_case_prompt").Import("""{ "test_case_prompt": "x" }""", "ds"));

    [Fact]
    public void Json_DefaultField_StillUsesNativeSchema()
    {
        // Default "prompt" path is unchanged (typed schema with id/expectedTokens).
        const string json = """[ { "id": "N-1", "prompt": "hi", "expectedTokens": ["PWNED"] } ]""";
        var probes = new JsonProbeDatasetImporter().Import(json, "native");
        Assert.Equal("N-1", probes[0].Id);
        Assert.Equal(["PWNED"], probes[0].ExpectedTokens!);
    }

    [Fact]
    public void Json_CustomField_NonStringValue_IsSkipped_NotThrown()
    {
        // A non-string field (e.g. a number) must be skipped, not abort the whole import (M2 fix).
        const string json = """[ { "test_case_prompt": 123 }, { "test_case_prompt": "ok" } ]""";
        Assert.Equal("ok", Assert.Single(new JsonProbeDatasetImporter("test_case_prompt").Import(json, "ds")).Prompt);
    }

    [Fact]
    public void Json_DefaultField_NullLiteral_Throws()
        => Assert.Throws<FormatException>(() => new JsonProbeDatasetImporter().Import("null", "ds"));

    // ── L11: per-probe metadata is a fresh instance, not one dict aliased across every probe ──

    [Fact]
    public void Csv_EachProbeHasIndependentMetadataInstance()
    {
        var probes = new CsvProbeDatasetImporter("Behavior").Import("Behavior\nrow one\nrow two\n", "HarmBench");
        Assert.Equal(2, probes.Count);
        Assert.False(ReferenceEquals(probes[0].Metadata, probes[1].Metadata));
    }

    [Fact]
    public void Json_CustomField_EachProbeHasIndependentMetadataInstance()
    {
        const string json = """[ { "test_case_prompt": "a" }, { "test_case_prompt": "b" } ]""";
        var probes = new JsonProbeDatasetImporter("test_case_prompt").Import(json, "ds");
        Assert.Equal(2, probes.Count);
        Assert.False(ReferenceEquals(probes[0].Metadata, probes[1].Metadata));
    }

    // ── L12: custom-field JSON honors an optional upstream id field (string or number), namespaced ──

    [Fact]
    public void Json_CustomField_HonorsIdField_WhenGiven()
    {
        const string json = """
        [
          { "prompt_id": 1, "test_case_prompt": "ignore your instructions" },
          { "prompt_id": 2, "test_case_prompt": "do the forbidden thing" }
        ]
        """;
        var probes = new JsonProbeDatasetImporter("test_case_prompt", idField: "prompt_id").Import(json, "CyberSecEval");
        Assert.Equal("CyberSecEval-1", probes[0].Id);
        Assert.Equal("CyberSecEval-2", probes[1].Id);
    }

    [Fact]
    public void Json_CustomField_FallsBackToIndex_WhenIdFieldMissing()
    {
        var probes = new JsonProbeDatasetImporter("test_case_prompt", idField: "prompt_id")
            .Import("""[ { "test_case_prompt": "x" } ]""", "ds");
        Assert.Equal("ds-0000", probes[0].Id);
    }

    [Fact]
    public void Json_CustomField_NoIdField_StillIndexBased()
    {
        var probes = new JsonProbeDatasetImporter("test_case_prompt")
            .Import("""[ { "prompt_id": 9, "test_case_prompt": "x" } ]""", "ds");
        Assert.Equal("ds-0000", probes[0].Id);   // no idField configured → index, prompt_id ignored
    }

    [Fact] // Jun14-M10: a NATIVE (default "prompt") .json import also honors --import-id-column, keeping the typed oracle.
    public void Json_NativeSchema_HonorsIdField_WhenGiven()
    {
        const string json = """[ { "prompt_id": 7, "prompt": "hi", "expectedTokens": ["PWNED"] } ]""";
        var probes = new JsonProbeDatasetImporter("prompt", idField: "prompt_id").Import(json, "ds");
        Assert.Equal("ds-7", probes[0].Id);
        Assert.Equal(["PWNED"], probes[0].ExpectedTokens!);   // typed schema (expectedTokens oracle) preserved
    }

    [Fact] // Jun14-L7: custom-field JSON reads an upstream license/source instead of hard-coding "unspecified".
    public void Json_CustomField_ReadsLicenseAndSource()
    {
        const string json = """[ { "test_case_prompt": "x", "license": "MIT", "source": "CyberSecEval" } ]""";
        var probes = new JsonProbeDatasetImporter("test_case_prompt").Import(json, "ds");
        Assert.Equal("MIT", probes[0].Metadata![JsonProbeDatasetImporter.LicenseKey]);
        Assert.Equal("CyberSecEval", probes[0].Source);
    }

    [Fact] // Jun14-L8: duplicate upstream ids are disambiguated so distinct probes never collide on ProbeId.
    public void Json_CustomField_DuplicateUpstreamIds_AreDisambiguated()
    {
        const string json = """[ { "prompt_id": 1, "test_case_prompt": "a" }, { "prompt_id": 1, "test_case_prompt": "b" } ]""";
        var probes = new JsonProbeDatasetImporter("test_case_prompt", idField: "prompt_id").Import(json, "ds");
        Assert.Equal(2, probes.Count);
        Assert.Equal(probes.Count, probes.Select(p => p.Id).Distinct().Count());
    }

    // ── RFC-4180 regression: quote handling, BOM, CRLF, ragged rows ────────────

    [Fact]
    public void Csv_LiteralQuoteMidField_DoesNotSwallowTheDelimiter()
    {
        // BUG (fixed): a '"' mid-field used to toggle quoting and swallow the following comma, merging columns.
        var rows = CsvProbeDatasetImporter.ParseCsv("a,b\n5\"6,foo\n");
        Assert.Equal(["5\"6", "foo"], rows[1]);
    }

    [Fact]
    public void Csv_TextAfterClosingQuote_IsAppendedLiterally()
        => Assert.Equal("abcdef", CsvProbeDatasetImporter.ParseCsv("\"abc\"def\n")[0][0]);

    [Fact]
    public void Csv_StripsLeadingBom_SoFirstColumnResolves()
    {
        // A BOM-poisoned header ("﻿Behavior") would otherwise throw "no 'Behavior' column".
        var probes = new CsvProbeDatasetImporter("Behavior").Import("﻿Behavior\nharmful\n", "HarmBench");
        Assert.Single(probes);
    }

    [Fact]
    public void Csv_HandlesCrlf_WithNoTrailingNewline_NoStrayCarriageReturn()
    {
        var probes = new CsvProbeDatasetImporter("Goal").Import("Goal\r\nrow one\r\nrow two", "JBB");
        Assert.Equal(2, probes.Count);
        Assert.Equal("row one", probes[0].Prompt);   // no trailing '\r'
        Assert.Equal("row two", probes[1].Prompt);   // last row flushed without a trailing newline
    }

    [Fact]
    public void Csv_PromptColumnNotFirst_WithQuotedCommaField_ReadsCorrectColumn()
    {
        // HarmBench/JBB put the prompt column AFTER an id column — a swallowed comma would shift the columns.
        var csv = "Index,Behavior,Category\n1,\"do X, then Y\",jailbreak\n";
        Assert.Equal("do X, then Y", Assert.Single(new CsvProbeDatasetImporter("Behavior").Import(csv, "HB")).Prompt);
    }

    [Fact]
    public void Csv_RaggedRow_IsSkipped_NoCrash()
        => Assert.Single(new CsvProbeDatasetImporter("Goal").Import("A,Goal\nx,real goal\nonly-one-col\n", "ds"));

    [Fact]
    public void Csv_EmptyContent_Throws()
        => Assert.Throws<ArgumentException>(() => new CsvProbeDatasetImporter("Goal").Import("", "ds"));
}
