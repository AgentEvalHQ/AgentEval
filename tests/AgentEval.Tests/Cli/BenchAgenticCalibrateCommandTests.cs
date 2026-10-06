// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Smoke tests for the <c>agenteval bench agentic calibrate</c> subcommand
/// (<see cref="BenchAgenticCalibrateCommand"/>). Phase-4 Task 4.6.
/// </summary>
[Collection("EnvVarTests")]
public class BenchAgenticCalibrateCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchAgenticCalibrateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-agentic-cal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _envSnapshot = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        ScrubEnv();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     _envSnapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      _envSnapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   _envSnapshot.Deployment);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", _envSnapshot.Stub);
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ScrubEnv()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);
    }

    private sealed class AlwaysPassEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
        }
    }

    [Fact]
    public async Task BenchAgenticCalibrate_NoProvider_ReturnsExitCode3()
    {
        var exit = await BenchAgenticCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchAgenticCalibrate_PartialAzureConfig_ReturnsExitCode3()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "test-key-only");
        // Missing endpoint + deployment → partial config → exit 2

        var exit = await BenchAgenticCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchAgenticCalibrate_PassingStubEvaluatorOverride_CompletesWithExitCode()
    {
        // Same shape as BenchEuAiActCalibrateCommandTests' end-to-end smoke test —
        // the override bypasses env-var resolution, calibration runs against the
        // embedded golden datasets, returns the gate exit code.
        var exit = await BenchAgenticCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: null,
            evaluatorOverride: new AlwaysPassEvaluator());

        Assert.True(exit is 0 or 9,
            $"Expected exit 0 or 9 (GateFailed); got {exit}.");

        // With no --out, the report goes to the workspace folder, never a repository-internal path.
        var written = Directory.GetFiles(Path.Combine(_root, ".agenteval", "calibration"), "agentic-calibration-*.md");
        Assert.Single(written);
    }

    [Fact]
    public async Task BenchAgenticCalibrate_ReportAndEveryRecord_NameTheJudge()
    {
        var outPath = Path.Combine(_root, "report-judge.md");
        var recordsPath = Path.Combine(_root, "records.jsonl");

        await BenchAgenticCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysPassEvaluator(),
            recordsPath: recordsPath,
            limitPerCategory: 1,
            evaluatorOverrideIdentity: new CalibrationJudgeIdentity("Test Provider", "test-model-7"));

        var content = await File.ReadAllTextAsync(outPath);
        Assert.Contains("Judge provider: Test Provider", content);
        Assert.Contains("Judge model: test-model-7", content);

        // Every per-case line names the judge too, beside the runner's own fields.
        var lines = (await File.ReadAllLinesAsync(recordsPath)).Where(l => l.Length > 0).ToList();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("Test Provider", doc.RootElement.GetProperty("judgeProvider").GetString());
            Assert.Equal("test-model-7", doc.RootElement.GetProperty("judgeModel").GetString());
            Assert.True(doc.RootElement.TryGetProperty("evaluatorKey", out _), "The runner's own fields must still be written.");
        }
    }

    [Fact]
    public async Task TheToolDataKeys_AreNotDispatched_OnTheseGoldens_AndTheReportSaysSo()
    {
        // B6c-7: the goldens carry no tool calls or definitions, so these keys are left out by KEY — not, as before,
        // scored on the records their own verdict let through.
        var outPath = Path.Combine(_root, "report-tool-keys.md");
        var recordsPath = Path.Combine(_root, "records-tool-keys.jsonl");

        await BenchAgenticCalibrateCommand.RunCoreAsync(
            rootOverride: _root, outPathOverride: outPath, evaluatorOverride: new AlwaysPassEvaluator(), recordsPath: recordsPath);

        var keys = (await File.ReadAllLinesAsync(recordsPath)).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(r => r.TryGetProperty("evaluatorKey", out var k) ? k.GetString() : r.GetProperty("EvaluatorKey").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(keys);
        Assert.All(BenchAgenticCalibrateCommand.s_notCalibratableOnTheseGoldens, k => Assert.DoesNotContain(k, keys));
        Assert.All(BenchAgenticCalibrateCommand.s_notCalibratableOnTheseGoldens,
            k => Assert.NotNull(AgentEval.Evals.EvalRegistry.Shared.TryGet(k)));   // still registered for every other use
        Assert.Contains("Carved out by key (not dispatched)", await File.ReadAllTextAsync(outPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACategoryEmptiedByCarveOuts_SaysSo_AndNeverCallsThemUnrouted()
    {
        // B6c-15 (found in the B12 pre-flight): memory and reasoning hold only carved-out keys, yet the report said their
        // entries "had no dispatch wiring (this means a new golden key is not yet routed)", and every category's table
        // repeated the same count as "Skipped (unknown key)".
        var outPath = Path.Combine(_root, "report-carved.md");

        await BenchAgenticCalibrateCommand.RunCoreAsync(
            rootOverride: _root, outPathOverride: outPath, evaluatorOverride: new AlwaysPassEvaluator());

        var report = await File.ReadAllTextAsync(outPath);
        Assert.Contains("## memory [SKIP]", report, StringComparison.Ordinal);   // every memory key is carved out
        Assert.Contains("carved out by key, not calibratable on these goldens (", report, StringComparison.Ordinal);
        // Reasoning is NOT empty: reasoning_correctness is dispatched and skips 4 of its 9 records (no reasoning-style
        // phrasing), so the key is excluded and the category is INCOMPLETE. B6c-15 pinned it as SKIP — review round 3 H1.
        Assert.Contains("## reasoning [INCOMPLETE]", report, StringComparison.Ordinal);
        Assert.Contains("intermediate_step_hallucination", report, StringComparison.Ordinal);
        Assert.DoesNotContain("not yet routed", report, StringComparison.Ordinal);       // every golden key is known
        Assert.DoesNotContain("Skipped (unknown key)", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 4, true)]     // nothing dispatched, carved keys only: SKIP
    [InlineData(0, 0, 4, 0, 5, 4, false)]    // a dispatched key excluded (INCOMPLETE) beside carved ones: not a SKIP
    [InlineData(0, 13, 0, 0, 0, 7, false)]   // every dispatched entry errored (INFRA-FAIL): not a SKIP
    [InlineData(5, 0, 0, 0, 0, 4, false)]
    public void ACategoryIsSkippedOnlyWhenNothingWasDispatched(
        int scored, int errored, int notMeasured, int inapplicable, int excludedMeasured, int carved, bool skip)
    {
        // Review round 3 H1: EntryCount counts scored pairs only, so the old test (EntryCount == 0) skipped categories
        // that were INCOMPLETE or INFRA-FAIL, and the gate passed them.
        var report = new AgentEval.Evals.Agentic.Calibration.CalibrationCategoryReport("c", scored, 0, 0, 0, 0,
            EvaluationFailures: errored, SkippedUnknownKey: carved, NotMeasured: notMeasured, NotApplicable: inapplicable)
        {
            ExcludedMeasuredRecords = excludedMeasured,
            ExcludedKeys = excludedMeasured + notMeasured > 0 ? ["k"] : [],
        };

        Assert.Equal(skip, BenchAgenticCalibrateCommand.IsAgentInfraSkipCategory("c", report));
    }

    [Fact]
    public async Task AJudgeThatAnswersOffItsRubricsScale_IsInfraFail_NotSkip()
    {
        // Every reply is {"score": 85}: off the scale of every 0–1 rubric, so those records are errors. Before the fix the
        // process category (all 0–1 rubrics) printed "[SKIP] nothing dispatched" and the gate could pass.
        var outPath = Path.Combine(_root, "report-offscale.md");
        var judge = new ChatClientEvaluator(new AgentEval.Tests.Evals.RubricJudgeTests.RecordingChatClient(_ => """{"score": 85}"""));

        var exit = await BenchAgenticCalibrateCommand.RunCoreAsync(rootOverride: _root, outPathOverride: outPath, evaluatorOverride: judge);

        var report = await File.ReadAllTextAsync(outPath);
        Assert.Contains("## process [INFRA-FAIL]", report, StringComparison.Ordinal);
        Assert.DoesNotContain("## process [SKIP]", report, StringComparison.Ordinal);
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void SplitUndispatched_TellsCarvedOutFromNotRouted()
    {
        var report = new AgentEval.Evals.Agentic.Calibration.CalibrationCategoryReport("c", 0, 0, 0, 0, 0, SkippedUnknownKey: 7)
        {
            SkippedKeys = new Dictionary<string, int>
            {
                ["f1_score"] = 2,                 // s_carveOutKeys
                ["unsafe_tool_use"] = 3,          // s_notCalibratableOnTheseGoldens
                ["brand_new_key"] = 2,            // nothing knows it
            },
        };

        var (carved, carvedKeys, notRouted, notRoutedKeys) = BenchAgenticCalibrateCommand.SplitUndispatched(report);

        Assert.Equal(5, carved);
        Assert.Equal("f1_score, unsafe_tool_use", carvedKeys);
        Assert.Equal(2, notRouted);
        Assert.Equal("brand_new_key", notRoutedKeys);
    }
}
