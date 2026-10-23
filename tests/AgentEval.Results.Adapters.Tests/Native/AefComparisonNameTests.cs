// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Tests.Native;

public sealed class AefComparisonNameTests : IDisposable
{
    private readonly string _out = AefTestRuns.TempPath("comparison-name");
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => AefTestRuns.Delete(_out);
    private string Write(AefContentCapture capture = AefContentCapture.Off, string? presetId = null, string? presetName = null) => AefCompositeRunWriter.Write(_out,
        new EvalResult(new("case", "Article 13 — recorded title", "test", "1"), new(1, null, "pass", true, .5, "none", null),
            new(null, null, null, null, null), new("code", null, null, null, null, 0, false), Start),
        AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:test", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "test:names", ContentCapture = capture,
            PresetComparisonId = presetId, PresetComparisonName = presetName,
        }, Start, Start.AddSeconds(1)).Directory;

    [Theory]
    [InlineData(AefContentCapture.On)]
    [InlineData(AefContentCapture.Off)]
    public void Read_RecordedName_PreservesExactDisplayName(AefContentCapture capture)
    {
        var directory = Write(capture);
        Assert.Equal("Article 13 — recorded title", Assert.Single(new AefComparisonReader().Read(directory).Scenarios).Name);
    }

    [Theory]
    [InlineData(AefContentCapture.On, true)]
    [InlineData(AefContentCapture.Off, true)]
    [InlineData(AefContentCapture.On, false)]
    [InlineData(AefContentCapture.Off, false)]
    public void Read_AlternateScenario_UsesOnlyCallerRecordedName(AefContentCapture capture, bool named)
    {
        var directory = Write(capture, "historical-case", named ? "Original scenario — title" : null);
        var scenario = Assert.Single(new AefComparisonReader().Read(directory).Scenarios);
        Assert.Equal("historical-case", scenario.Id);
        Assert.Equal(named ? "Original scenario — title" : "historical-case", scenario.Name);
    }

    [Fact]
    public void Write_PresetNameWithoutIdentity_RefusesBeforeCreatingRun()
    {
        Assert.Contains("comparison identity", Assert.Throws<ArgumentException>(() => Write(presetName: "orphan label")).Message);
        Assert.False(Directory.Exists(_out));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("boolean")]
    [InlineData("number")]
    [InlineData("object")]
    public void Read_ResealedName_UsesOlderFallbackOrRefusesMalformedValue(string shape)
    {
        var directory = Write();
        var line = Assert.Single(AefTestRuns.Results(directory));
        var metadata = line["ext"]!["agenteval.comparison"]!.AsObject();
        if (shape == "absent") metadata.Remove("name");
        else metadata["name"] = shape switch
        {
            "null" => null, "boolean" => JsonValue.Create(false), "number" => JsonValue.Create(1), _ => new JsonObject(),
        };
        File.WriteAllText(Path.Combine(directory, "results.ndjson"), line.ToJsonString(new() { WriteIndented = false }) + "\n");
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(directory).Outcome);
        if (shape is "absent" or "null")
            Assert.Equal("case", Assert.Single(new AefComparisonReader().Read(directory).Scenarios).Name);
        else Assert.Contains("comparison name", Assert.Throws<InvalidDataException>(() => new AefComparisonReader().Read(directory)).Message);
    }
}
