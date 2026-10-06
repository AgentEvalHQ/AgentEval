// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// The required-component rule (#203) on the real GDPR and EU AI Act presets. A first version keyed the parent's
/// block on a nested composite's label <c>warn</c>, which is just as often a MEASURED medium-severity fail: with one
/// article failing at a time, GDPR Standard warned on 10 of 29 medium/low failures while the 19 high/critical ones
/// still passed (review round 2, H-A). Every scenario here is measured, so no attestation gap exists anywhere and the
/// rule must not fire; and a worse failure must never read better than a milder one.
/// </summary>
/// <remarks>
/// The judge returns a fixed score per scenario so the test drives the verdict logic only; it claims nothing about
/// any agent or judge quality.
/// </remarks>
public class CompositeAttestationPresetTests
{
    /// <summary>Scores 50 for the scenarios of the article under test, 100 for every other one.</summary>
    private sealed class TargetedJudge : IEvaluator
    {
        public HashSet<string> Fail { get; set; } = new(StringComparer.Ordinal);

        public int FailScore { get; set; } = 50;

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            var score = Fail.Contains(string.Join("\u0001", list)) ? FailScore : 100;
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = score,
                Summary = "fixed",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = score >= 70, Explanation = "fixed" }).ToList(),
            });
        }
    }

    private static readonly EvalInput Input = new(Query: "q", Response: "r");

    private static int SeverityRank(string? s) => s?.ToLowerInvariant() switch
    {
        "low" => 1, "medium" => 2, "high" => 3, "critical" => 4, _ => 0,
    };

    private static int VerdictRank(string label) => label switch { "pass" => 0, "warn" => 1, _ => 2 };

    private static IEnumerable<EvalResult> Walk(EvalResult r)
    {
        yield return r;
        foreach (var s in r.Details.SubResults ?? [])
            foreach (var d in Walk(s))
                yield return d;
    }

    private static async Task<List<(string Article, int Severity, EvalResult Result)>> SweepAsync(string pack, bool auditGrade)
    {
        var judge = new TargetedJudge();
        var sweep = new List<(string, int, EvalResult)>();
        if (pack == "gdpr")
        {
            var loader = new AgentEval.Compliance.Gdpr.Articles.Loading.ArticleScenarioYamlLoader();
            var scenarios = new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(judge, judgeModel: "fixed");
            var registry = new AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry(
                loader, new AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder(scenarios));
            var preset = auditGrade ? GdprBenchmark.AuditGrade(registry) : GdprBenchmark.Standard(registry);
            foreach (var id in registry.All.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var spec = registry.GetSpec(id);
                judge.Fail = spec.Scenarios.Select(s => string.Join("\u0001", s.EvaluationCriteria)).ToHashSet(StringComparer.Ordinal);
                sweep.Add((id, SeverityRank(spec.Metadata.Severity), await preset.EvaluateAsync(Input)));
            }
        }
        else
        {
            var loader = new AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader();
            var scenarios = new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(judge, judgeModel: "fixed");
            var registry = new AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry(
                loader, new AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder(scenarios));
            var preset = auditGrade ? EuAiActBenchmark.AuditGrade(registry) : EuAiActBenchmark.Standard(registry);
            foreach (var id in registry.All.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var spec = registry.GetSpec(id);
                judge.Fail = spec.Scenarios.Select(s => string.Join("\u0001", s.EvaluationCriteria)).ToHashSet(StringComparer.Ordinal);
                sweep.Add((id, SeverityRank(spec.Metadata.Severity), await preset.EvaluateAsync(Input)));
            }
        }
        return sweep;
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("euaiact", false)]
    [InlineData("euaiact", true)]
    public async Task OneArticleFailing_AMeasuredFailureIsNeverReadAsAnAttestationGap(string pack, bool auditGrade)
    {
        var sweep = await SweepAsync(pack, auditGrade);

        Assert.NotEmpty(sweep);
        foreach (var (article, _, result) in sweep)
        {
            // Every scenario was measured, so nothing anywhere in the tree withheld a pass for a component that did not run.
            Assert.All(Walk(result), r => Assert.NotEqual(MeasurementState.NotMeasured, r.Score.Measurement));
            Assert.DoesNotContain("could not attest", result.Details.Summary ?? "", StringComparison.Ordinal);
            Assert.True(result.Score.Label is "pass" or "warn" or "fail", $"{article}: unexpected label {result.Score.Label}");
        }
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("euaiact", false)]
    [InlineData("gdpr", true)]
    [InlineData("euaiact", true)]
    public async Task OneArticleFailing_TheVerdictFollowsTheDocsTable(string pack, bool auditGrade)
    {
        // B4 (#203 review): Standard's 0.85 weighted average absorbed every single-article failure — 19 high/critical
        // GDPR ones read PASS, though the GDPR docs' verdict table says FAIL for any high or critical article failure.
        // The threshold pass is now capped by severity: high/critical → fail, medium → warn, none/low → pass.
        // B6d: the docs say EVERY preset applies the table; AuditGrade's CapByWorst caps only high/critical, so a
        // medium article failing among ~20 averaged to ≥ 0.90 = PASS.
        var sweep = await SweepAsync(pack, auditGrade);

        // Only articles the preset contains AND that really failed at this score: an article with a low pass threshold
        // (EU GPAI self-provenance passes at 0.50) is not failing here, so the preset's pass is correct for it.
        var inPreset = sweep
            .Where(s => Walk(s.Result).Any(r => r.Metric.Key == s.Article && !r.Score.Passed))
            .ToList();
        Assert.NotEmpty(inPreset);

        foreach (var (article, severity, result) in inPreset)
        {
            // A failing leaf's severity is the higher of the article's and the score's (AtomicLlmEval never lowers a
            // score-derived severity); at this sweep's score of 50 the score's is "medium". So a low or medium article
            // failing reads WARN, and a high or critical one FAIL.
            var expected = severity >= 3 ? "fail" : "warn";
            Assert.True(expected == result.Score.Label,
                $"{article} (severity rank {severity}) failing → {result.Score.Label}, the docs promise {expected}");
        }
        Assert.Contains(inPreset, s => s.Severity >= 3);   // the sweep really exercised high/critical articles
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("euaiact", false)]
    [InlineData("euaiact", true)]
    public async Task OneArticleFailing_AWorseFailureNeverReadsBetterThanAMilderOne(string pack, bool auditGrade)
    {
        var sweep = await SweepAsync(pack, auditGrade);

        // For every pair of articles, the more severe failure gets a verdict at least as bad.
        foreach (var worse in sweep)
            foreach (var milder in sweep.Where(m => m.Severity < worse.Severity))
                Assert.True(
                    VerdictRank(worse.Result.Score.Label) >= VerdictRank(milder.Result.Score.Label),
                    $"{worse.Article} (severity {worse.Severity}) → {worse.Result.Score.Label}, but the milder " +
                    $"{milder.Article} (severity {milder.Severity}) → {milder.Result.Score.Label}");
    }

    // ── B6c-3 (mid-branch review): a failing SCENARIO inside an article that still passes is not a preset failure ────
    // The verdict read severity from every required part, passed or not, and a passing article still carried its failing
    // scenario's severity: in 54 of 58 GDPR Standard cases the article met its own threshold and the preset read FAIL or
    // WARN with no article failing — and Art 16 passing with one scenario at 0.30 read FAIL while Art 16 failing as a
    // whole read WARN. The docs' table: PASS = every article met its own pass threshold.

    private static async Task<List<(string Article, EvalResult? ArticleResult, EvalResult Root)>> OneScenarioSweepAsync(string pack, bool auditGrade)
    {
        var judge = new TargetedJudge { FailScore = 30 };
        var sweep = new List<(string, EvalResult?, EvalResult)>();
        CompositeEval preset;
        List<(string Id, string FirstScenarioKey)> articles;
        if (pack == "gdpr")
        {
            var loader = new AgentEval.Compliance.Gdpr.Articles.Loading.ArticleScenarioYamlLoader();
            var scenarios = new AgentEval.Compliance.Gdpr.Articles.Building.ScenarioToAtomicEval(judge, judgeModel: "fixed");
            var registry = new AgentEval.Compliance.Gdpr.Articles.ArticlesRegistry(
                loader, new AgentEval.Compliance.Gdpr.Articles.Building.ArticleCompositeBuilder(scenarios));
            preset = auditGrade ? GdprBenchmark.AuditGrade(registry) : GdprBenchmark.Standard(registry);
            articles = registry.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(id => (id, string.Join("\u0001", registry.GetSpec(id).Scenarios[0].EvaluationCriteria)))
                .ToList();
        }
        else
        {
            var loader = new AgentEval.Compliance.EuAiAct.Articles.Loading.ArticleScenarioYamlLoader();
            var scenarios = new AgentEval.Compliance.EuAiAct.Articles.Building.ScenarioToAtomicEval(judge, judgeModel: "fixed");
            var registry = new AgentEval.Compliance.EuAiAct.Articles.EuAiActArticlesRegistry(
                loader, new AgentEval.Compliance.EuAiAct.Articles.Building.ArticleCompositeBuilder(scenarios));
            preset = auditGrade ? EuAiActBenchmark.AuditGrade(registry) : EuAiActBenchmark.Standard(registry);
            articles = registry.All.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(id => (id, string.Join("\u0001", registry.GetSpec(id).Scenarios[0].EvaluationCriteria)))
                .ToList();
        }

        foreach (var (id, firstScenario) in articles)
        {
            judge.Fail = [firstScenario];
            var root = await preset.EvaluateAsync(Input);
            sweep.Add((id, Walk(root).FirstOrDefault(r => r.Metric.Key == id), root));
        }
        return sweep;
    }

    [Theory]
    [InlineData("gdpr", false)]
    [InlineData("gdpr", true)]
    [InlineData("euaiact", false)]
    [InlineData("euaiact", true)]
    public async Task OneScenarioFailing_TheVerdictFollowsTheArticles_NotTheScenarios(string pack, bool auditGrade)
    {
        var sweep = await OneScenarioSweepAsync(pack, auditGrade);
        var wrong = new List<string>();

        foreach (var (article, articleResult, root) in sweep)
        {
            if (articleResult is null)
                continue;   // not in this preset
            if (articleResult.Score.Passed && root.Score.Label != "pass")
                wrong.Add($"{article} passed its own threshold ({articleResult.Score.Value:0.00}), yet the preset read {root.Score.Label}");
            if (!articleResult.Score.Passed && root.Score.Label == "pass")
                wrong.Add($"{article} failed ({articleResult.Score.Value:0.00}), yet the preset read pass");
        }

        Assert.True(sweep.Any(s => s.ArticleResult is { Score.Passed: true }), "the sweep must exercise articles that still pass");
        Assert.True(wrong.Count == 0, $"{wrong.Count} wrong verdict(s): " + string.Join("; ", wrong.Take(6)));
    }
}
