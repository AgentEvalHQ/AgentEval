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

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            var score = Fail.Contains(string.Join("\u0001", list)) ? 50 : 100;
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
}
