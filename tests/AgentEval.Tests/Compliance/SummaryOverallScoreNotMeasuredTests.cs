// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.Tests.Census;
using Json.Schema;
using Xunit;
using GR = AgentEval.Compliance.Gdpr.Reporting;
using ER = AgentEval.Compliance.EuAiAct.Reporting;
using AR = AgentEval.Evals.Agentic.Reporting;
using static AgentEval.Tests.Census.CensusFakes;

namespace AgentEval.Tests.Compliance;

public sealed class SummaryOverallScoreNotMeasuredTests
{
    private sealed class NeverCalledJudge : AgentEval.Core.IEvaluator
    {
        public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => throw new InvalidOperationException("Summary generation must not call a judge.");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IEnumerable<object[]> MissingCases() =>
        from kind in new[] { "gdpr", "eu", "agentic" }
        from shape in new[] { "empty", "skipped", "error", "inapplicable", "withheld" }
        select new object[] { kind, shape };

    internal static EvalResult Tree(string shape) => shape switch
    {
        "empty" => Root("summary-test", Skipped()),
        "skipped" => Root("summary-test", Skipped(), Leaf("missing", Skipped())),
        "error" => Root("summary-test", Error(), Leaf("error", Error())),
        "inapplicable" => Root("summary-test", Inapplicable()),
        "withheld" => Root("summary-test", new EvalScore(0.8, null, "warn", false, null, "none", null)
            { Measurement = MeasurementState.NotMeasured }, Leaf("measured", Pass(0.9))),
        "zero" => Root("summary-test", Fail(0)),
        _ => Root("summary-test", Pass(0.9)),
    };

    internal static AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry GdprRegistry() => new(
        new AgentEval.Compliance.Gdpr.Articles.Loading.ArticleScenarioYamlLoader(),
        new AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder(
            new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), "offline")));

    internal static AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry EuRegistry() => new(
        new AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader(),
        new AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder(
            new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(new NeverCalledJudge(), "offline")));

    private static double? Score(string kind, EvalResult root) => kind switch
    {
        "gdpr" => new GR.SummaryBuilder(GdprRegistry()).Build(root).OverallScore,
        "eu" => new ER.SummaryBuilder(EuRegistry()).Build(root).OverallScore,
        _ => new AR.AgenticSummaryBuilder().Build(root).OverallScore,
    };

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void Build_UnmeasuredRoot_HasNoOverallScore(string kind, string shape)
    {
        var root = Tree(shape);
        Assert.Null(Score(kind, root));
        Assert.False(RunSummary(kind, root).Metrics.ContainsKey("overallScore"));
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public void Build_MeasuredRoot_PreservesZeroAndNonzero(string kind)
    {
        Assert.Equal(0, Score(kind, Tree("zero")));
        Assert.Equal(0.9, Score(kind, Tree("pass")));
        foreach (var shape in new[] { "zero", "pass" })
            Assert.Equal(Score(kind, Tree(shape)), RunSummary(kind, Tree(shape)).Metrics["overallScore"]);
    }

    private static RunSummary RunSummary(string kind, EvalResult root) => kind switch
    {
        "gdpr" => AgentEval.Compliance.Gdpr.Articles.GdprBenchmarkRunner.BuildSummary(root, "run"),
        "eu" => AgentEval.Compliance.EuAiAct.Articles.EuAiActBenchmarkRunner.BuildSummary(root, "run"),
        _ => AgentEval.Evals.Agentic.Composition.AgenticBenchmarkRunner.BuildSummary(root, "run"),
    };

    internal static async Task<object> Report(string kind, EvalResult root)
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "summary-test");
        var manifest = await store.StartRunAsync(subject, new RunContext("test", "test", "offline", null, null, Kind: "compliance"));
        await store.CompleteRunAsync(manifest, new RunSummary("1.0", manifest.Run.RunId, "SKIPPED", new(0, 0, 0, 0), new Dictionary<string, double>()));
        return kind switch
        {
            "gdpr" => await new GR.GDPRComplianceReporter(GdprRegistry()).SaveReportAsync(store, subject, manifest.Run.RunId, root),
            "eu" => await new ER.EuAiActComplianceReporter(EuRegistry()).SaveReportAsync(store, subject, manifest.Run.RunId, root),
            _ => await new AR.AgenticBenchmarkReporter().SaveReportAsync(store, subject, manifest.Run.RunId, root),
        };
    }

    internal static string Markdown(object report) => report switch
    {
        GR.GdprComplianceEvidence g => new GR.MarkdownRenderer().Render(g),
        ER.EuAiActComplianceEvidence e => new ER.MarkdownRenderer().Render(e),
        AR.AgenticBenchmarkResult a => new AR.AgenticMarkdownRenderer().Render(a),
        _ => throw new ArgumentException("Unexpected report"),
    };

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public async Task SaveReport_UnmeasuredRoot_PersistsExplicitNullAndRendersNoScore(string kind)
    {
        var report = await Report(kind, Root("summary-test", Error()));
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
        Assert.True(json["summary"]!.AsObject().ContainsKey("overallScore"));
        Assert.Null(json["summary"]!["overallScore"]);
        Assert.Contains("(score n/a", Markdown(report));
        Assert.DoesNotMatch(@"score 0\s?%", Markdown(report));
    }

    [Theory]
    [InlineData("gdpr")]
    [InlineData("eu")]
    [InlineData("agentic")]
    public async Task Render_LegacySkippedPlaceholder_DoesNotPrintZero(string kind)
    {
        var report = await Report(kind, Tree("empty"));
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
        json["summary"]!["overallScore"] = 0;
        json["summary"]!["overallStatus"] = "SKIPPED";
        var legacy = JsonSerializer.Deserialize(json.ToJsonString(), report.GetType(), JsonOptions)!;
        Assert.Contains("(score n/a", Markdown(legacy));
    }

    [Theory]
    [InlineData("gdpr", "gdpr-evidence.schema.json")]
    [InlineData("eu", "eu-ai-act-evidence.schema.json")]
    [InlineData("agentic", "agentic-result.schema.json")]
    public async Task Schema_OverallScore_AllowsNullButRequiresANumberOrNull(string kind, string schemaName)
    {
        var report = await Report(kind, Tree("pass"));
        var assembly = report.GetType().Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(schemaName, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        var schema = JsonSchema.FromText(await reader.ReadToEndAsync());
        var json = JsonSerializer.SerializeToNode(report, JsonOptions)!;
        var summary = json["summary"]!.AsObject();
        summary["overallScore"] = null;
        Assert.True(schema.Evaluate(json).IsValid);
        summary.Remove("overallScore");
        Assert.False(schema.Evaluate(json).IsValid);
        summary["overallScore"] = "0.9";
        Assert.False(schema.Evaluate(json).IsValid);
        summary["overallScore"] = 1.5;
        Assert.False(schema.Evaluate(json).IsValid);
        summary["overallScore"] = 0;
        Assert.True(schema.Evaluate(json).IsValid);
    }
}
