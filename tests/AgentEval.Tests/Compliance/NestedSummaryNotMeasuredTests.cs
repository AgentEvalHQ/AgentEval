// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.RedTeam.Reporting.Compliance;
using Json.Schema;
using Xunit;
using static AgentEval.Tests.Census.CensusFakes;
using static AgentEval.Tests.Compliance.SummaryOverallScoreNotMeasuredTests;

namespace AgentEval.Tests.Compliance;

public sealed class NestedSummaryNotMeasuredTests
{
    public static IEnumerable<object[]> MissingCases() =>
        from kind in new[] { "gdpr", "eu", "agentic" }
        from layered in new[] { false, true }
        from state in new[] { "skipped", "error", "inapplicable", "withheld" }
        select new object[] { kind, layered, state };

    internal static EvalResult NestedTree(string kind, bool layered, string state)
    {
        var score = state switch
        {
            "error" => Error(), "inapplicable" => Inapplicable(), "zero" => Fail(0), "pass" => Pass(0.9),
            "withheld" => new EvalScore(0.8, null, "warn", false, null, "none", null) { Measurement = MeasurementState.NotMeasured },
            _ => Skipped(),
        };
        var key = kind == "gdpr" ? GdprRegistry().All.Keys.First()
            : kind == "eu" ? EuRegistry().All.Keys.First() : "token_usage";
        var leaf = Root(key, score, Leaf("scenario", score)) with { Metric = new(key, key, "operational", "1.0.0") };
        var child = layered ? Root(kind == "agentic" ? "operational" : "PillarTest", score, leaf) : leaf;
        return Root("nested-test", score, child);
    }

    internal static IEnumerable<JsonObject> NestedScores(JsonNode report) =>
        report["summary"]!.AsObject().Where(p => p.Key.StartsWith("per", StringComparison.Ordinal))
            .SelectMany(p => p.Value!.AsObject().Select(v => v.Value!.AsObject()));

    [Theory]
    [MemberData(nameof(MissingCases))]
    public async Task Report_UnmeasuredNestedNodes_HaveExplicitNullScores(string kind, bool layered, string state)
    {
        var report = await Report(kind, NestedTree(kind, layered, state));
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
        var rows = NestedScores(json).ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => { Assert.True(row.ContainsKey("score")); Assert.Null(row["score"]); });
        if (kind != "agentic")
            Assert.All(json["base"]!["controls"]!.AsArray(), c =>
            { Assert.True(c!.AsObject().ContainsKey("passRate")); Assert.Null(c["passRate"]); });
        var markdown = Markdown(report);
        Assert.Contains("n/a", markdown);
        Assert.DoesNotMatch(@"\|\s*0\s?%\s*\|", markdown);
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public async Task Report_MeasuredZeroAndLegacyNumericScores_ArePreserved(string kind)
    {
        foreach (var state in new[] { "zero", "pass" })
        {
            var report = await Report(kind, NestedTree(kind, true, state));
            var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
            Assert.All(NestedScores(json), row => Assert.Equal(state == "zero" ? 0 : 0.9, row["score"]!.GetValue<double>()));
            var legacy = JsonSerializer.Deserialize(json.ToJsonString(), report.GetType(), JsonOptions)!;
            Assert.Equal(Markdown(report), Markdown(legacy));
        }
    }

    [Theory]
    [InlineData("gdpr", "gdpr-evidence.schema.json")]
    [InlineData("eu", "eu-ai-act-evidence.schema.json")]
    [InlineData("agentic", "agentic-result.schema.json")]
    public async Task Schema_NestedScores_AcceptsNullAndMeasuredNumbersOnly(string kind, string name)
    {
        var report = await Report(kind, NestedTree(kind, true, "pass"));
        var schema = LoadSchema(report.GetType().Assembly, name);
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
        foreach (var row in NestedScores(json)) row["score"] = null;
        Assert.True(schema.Evaluate(json).IsValid);
        var first = NestedScores(json).First();
        foreach (var invalid in new JsonNode?[] { JsonValue.Create("0.8"), JsonValue.Create(1.2), JsonValue.Create(-0.2) })
        { first["score"] = invalid; Assert.False(schema.Evaluate(json).IsValid); }
        first.Remove("score");
        Assert.False(schema.Evaluate(json).IsValid);
        first["score"] = 0;
        Assert.True(schema.Evaluate(json).IsValid);
    }

    [Fact]
    public async Task EvidenceSchema_ControlRate_AllowsExplicitNull()
    {
        var report = await Report("gdpr", NestedTree("gdpr", false, "pass"));
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!["base"]!;
        var schema = LoadSchema(typeof(FileSystemOutputStore).Assembly, ".evidence.schema.json");
        var control = json["controls"]![0]!.AsObject();
        control["passRate"] = null;
        Assert.True(schema.Evaluate(json).IsValid);
        control.Remove("passRate");
        Assert.False(schema.Evaluate(json).IsValid);
        control["passRate"] = 0;
        Assert.True(schema.Evaluate(json).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void RedTeamControls_NoConclusiveProbes_HaveNoPassRate(int total)
    {
        var control = new ControlStatus { Control = new() { ControlId = "c", ControlName = "c", Description = "c", Framework = "test", RelevantAttacks = [] }, TotalTests = total, ConclusiveTests = 0 };
        var owasp = new OWASPCategoryStatus { Id = "c", Name = "c", TotalTests = total, ConclusiveTests = 0 };
        var mitre = new MITRETechniqueStatus { Id = "c", Name = "c", TacticId = "t", TacticName = "t", TotalTests = total, ConclusiveTests = 0 };
        Assert.Null((double?)control.PassRate);
        Assert.Null((double?)owasp.PassRate);
        Assert.Null((double?)mitre.PassRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.9)]
    public void AgenticCategory_PartlyMeasuredError_PreservesMeasuredPart(double value)
    {
        var root = Root("flat", Error(), Leaf("measured", value == 0 ? Fail(0) : Pass(value), "operational"), Leaf("error", Error(), "operational"));
        var summary = new AgentEval.Evals.Agentic.Reporting.AgenticSummaryBuilder().Build(root);
        Assert.Equal(value, Assert.Single(summary.PerCategory).Value.Score);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ControlCoverage_EmptyIsAbsent_InconclusiveRunIsMeasuredZero(int total)
    {
        var control = new ControlStatus { Control = new() { ControlId = "c", ControlName = "c", Description = "c", Framework = "test", RelevantAttacks = [] }, TotalTests = total, ConclusiveTests = 0 };
        if (total == 0) Assert.Null((double?)control.Coverage);
        else Assert.Equal<double?>(0, control.Coverage);
    }

    [Fact]
    public void RedTeamControls_AllMeasuredFailures_PreserveZero()
    {
        var control = new ControlStatus { Control = new() { ControlId = "c", ControlName = "c", Description = "c", Framework = "test", RelevantAttacks = [] }, TotalTests = 5 };
        var owasp = new OWASPCategoryStatus { Id = "c", Name = "c", TotalTests = 5 };
        var mitre = new MITRETechniqueStatus { Id = "c", Name = "c", TacticId = "t", TacticName = "t", TotalTests = 5 };
        Assert.Equal<double?>(0, control.PassRate);
        Assert.Equal<double?>(0, owasp.PassRate);
        Assert.Equal<double?>(0, mitre.PassRate);
    }

    private static JsonSchema LoadSchema(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    [Fact]
    public void EvidenceControl_UnmeasuredRate_PersistsExplicitNull()
    {
        var control = UnmeasuredControl();
        var json = JsonSerializer.SerializeToNode(control, JsonOptions)!.AsObject();
        Assert.True(json.ContainsKey("passRate"));
        Assert.Null(json["passRate"]);
        Assert.Null(JsonSerializer.Deserialize<EvidenceControl>(json.ToJsonString(), JsonOptions)!.PassRate);
    }

    private static EvidenceControl UnmeasuredControl() =>
        (EvidenceControl)typeof(EvidenceControl).GetConstructors().Single().Invoke(
            ["control", "Control", "SKIPPED", null, Array.Empty<string>(), null, null]);

#if NET10_0_OR_GREATER
    [Fact]
    public async Task Matrix_UnmeasuredControl_PreservesAbsence()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "nested-matrix");
        var manifest = await store.StartRunAsync(subject, new RunContext("test", "test", "offline", null, null, Kind: "compliance"));
        await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "PENDING", new(0, 0, 0, 0), new Dictionary<string, double>()));
        manifest = (await store.GetRunManifestAsync(manifest.Run.RunId))!;
        var evidence = new ComplianceEvidence("1.0", "gdpr", subject, DateTimeOffset.UtcNow,
            new(manifest.Run.RunId, manifest.ContentHash), [UnmeasuredControl()], new(1, 0, 0, 0, "SKIPPED"), new("test", null, "test", "test"));
        await store.SaveComplianceEvidenceAsync("gdpr", subject, evidence);
        var matrix = await new AgentEval.MissionControl.Services.ComplianceMatrixService(store).BuildMatrixAsync("gdpr");
        Assert.Null((double?)Assert.Single(matrix.Cells).PassRate);
    }
#endif
}
