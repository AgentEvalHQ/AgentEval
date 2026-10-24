// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;
using AgentEval.Samples;
using AgentEval.Samples.Benchmarks;
using Xunit;

namespace AgentEval.Tests.Samples;

[Collection("ConsoleTests")]
public sealed class PartialBenchmarkReportingTests : IDisposable
{
    public PartialBenchmarkReportingTests() => SampleOutcome.Reset();
    public void Dispose() => SampleOutcome.Reset();

    [Fact]
    public async Task PartialBenchmark_SampleOutcome_RequiresMeasuredVerdict()
    {
        var result = await PartialBenchmark();

        SampleOutcome.RecordVerdict("performance", result);

        Assert.Equal(SampleOutcome.NotMeasured, SampleOutcome.ExitCode);
        Assert.DoesNotContain(SampleOutcome.Records, r => r.Code == SampleOutcome.Warning);
    }

    [Fact]
    public async Task PartialBenchmark_PersistedSummary_OmitsUnavailableScoreAndCountsTheGap()
    {
        var summary = BenchmarkSampleReporting.BuildRunSummary(await PartialBenchmark(), "control");

        Assert.False(summary.Metrics!.ContainsKey("overallScore"));
        Assert.Equal(1, summary.Stats.Skipped);
        Assert.Equal(0, summary.Stats.Warnings);
        Assert.Equal(2, summary.Stats.Passed);
        Assert.Equal("WARN", summary.Verdict);
    }

    [Fact]
    public async Task PartialBenchmark_Console_QualifiesUnavailableScore()
    {
        Assert.Contains("Score: not measured", BenchmarkSampleReporting.FormatVerdict(await PartialBenchmark()));
    }

    [Fact]
    public async Task PartialBenchmark_Html_QualifiesRootAndLatencyScores()
    {
        var result = await PartialBenchmark();
        var html = Encoding.UTF8.GetString(await new HtmlEvalResultRenderer().RenderAsync(result, Options()));

        Assert.Contains("<span class=\"score\">not measured</span>", html);
        Assert.Equal(2, Regex.Matches(html, "<span class=\"score-inline\">not measured</span>").Count);
        Assert.Contains("p99_ms", html); // Available diagnostics remain visible.
        Assert.Contains("perf_cost", html);
    }

    [Fact]
    public async Task PartialBenchmark_Pdf_QualifiesCoverComponentAndLeafScores()
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(
            await new PdfEvalResultRenderer().RenderAsync(await PartialBenchmark(), Options()));
        static string Compact(string s) => Regex.Replace(s, @"\s+", "");

        Assert.Contains("OVERALL:WARN(notmeasured)", Compact(doc.GetPage(1).Text));
        Assert.Contains("perf_latencynotmeasuredWARN", Compact(doc.GetPage(2).Text));
        Assert.Contains(doc.GetPages(), p => Compact(p.Text).Contains("Score:notmeasured"));
    }

    [Theory]
    [InlineData("pass", 1d, true, SampleOutcome.Completed)]
    [InlineData("warn", 0.8d, false, SampleOutcome.Warning)]
    [InlineData("fail", 0d, false, SampleOutcome.Failed)]
    public void MeasuredRoot_KeepsItsOwnVerdict(string label, double value, bool passed, int expected)
    {
        SampleOutcome.RecordVerdict("measured", Result(label, value, passed));

        Assert.Equal(expected, SampleOutcome.ExitCode);
        Assert.Equal(value, BenchmarkSampleReporting.BuildRunSummary(Result(label, value, passed), "control").Metrics!["overallScore"]);
    }

    [Fact]
    public async Task MeasuredRootFailure_WithPartialBenchmarkGap_StillFails()
    {
        var partial = await PartialBenchmark();
        SampleOutcome.RecordVerdict("performance", partial with { Score = new(0, null, "fail", false, null, "high", null) });

        Assert.Equal(SampleOutcome.Failed, SampleOutcome.ExitCode);
    }

    [Fact]
    public void InapplicableLeaf_IsNotARequiredMeasurementGap()
    {
        var leaf = Result("inapplicable", 0, false);
        SampleOutcome.RecordVerdict("applicable checks", Result("pass", 1, true) with
        {
            Details = new(null, null, null, [leaf], "test"),
        });

        Assert.Equal(SampleOutcome.Completed, SampleOutcome.ExitCode);
    }

    [Fact]
    public async Task MeasuredZero_RenderersKeepTheActualZero()
    {
        var result = Result("fail", 0, false);
        var html = Encoding.UTF8.GetString(await new HtmlEvalResultRenderer().RenderAsync(result, Options()));
        Assert.Contains($"<span class=\"score\">{0d.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)}</span>", html);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(await new PdfEvalResultRenderer().RenderAsync(result, Options()));
        Assert.Contains("OVERALL:FAIL(0%)", Regex.Replace(doc.GetPage(1).Text, @"\s+", ""));
    }

    private static EvalResult Result(string label, double value, bool passed) => new(
        new("control", "Control", "test", "1"), new(value, null, label, passed, null, "none", null),
        new(null, null, null, null, null), new("code", null, null, null, null, null, false), DateTimeOffset.UtcNow);

    private static EvalResultRenderOptions Options() =>
        new(Subject: new SubjectIdentity(SubjectKind.Agent, "offline performance control"), IncludeProvenance: false);

    private static async Task<EvalResult> PartialBenchmark()
    {
        var benchmark = new PerformanceBenchmark(new PartlyFailingAgent(), new PerformanceBenchmarkOptions
        {
            Verbose = false, DelayBetweenIterations = TimeSpan.Zero,
            EvaluateOptions = new PerformanceBenchmarkEvaluateOptions
            {
                P99LatencyThresholdMs = 60_000, LatencyIterationsPerPrompt = 3, WarmupIterations = 0,
                MinThroughputRps = 0.01, ThroughputConcurrentRequests = 1,
                ThroughputDuration = TimeSpan.FromMilliseconds(50), MaxCostUSD = 1,
                CompositePassThreshold = 0.6, CostModelName = "gpt-4o",
            },
        });
        var result = await benchmark.EvaluateAsync(new EvalInput("hello"));
        Assert.Equal("warn", result.Score.Label);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.Measurement);
        Assert.All(result.Details.SubResults!.Where(r => r.Metric.Key != "perf_latency"),
            r => Assert.Equal(MeasurementState.Measured, r.Score.Measurement));
        return result;
    }

    private sealed class PartlyFailingAgent : IEvaluableAgent
    {
        private int _calls;
        public string Name => "partial latency control";
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _calls) <= 2
                ? Task.FromException<AgentResponse>(new HttpRequestException("synthetic failed iteration"))
                : Task.FromResult(new AgentResponse
                {
                    Text = "answer", ModelId = "gpt-4o",
                    TokenUsage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 },
                });
    }
}
