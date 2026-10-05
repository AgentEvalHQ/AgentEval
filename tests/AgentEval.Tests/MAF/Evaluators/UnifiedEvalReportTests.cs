// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.MAF.Evaluators;
using Xunit;
using static AgentEval.Tests.MAF.Evaluators.HybridEvalTestHelpers;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Unit coverage for <see cref="UnifiedEvalReport"/>: one source-tagged branch per source, the Foundry
/// portal link surfaced as evidence, and the AgentEval-local branch preserving its full composite hierarchy.
/// </summary>
public class UnifiedEvalReportTests
{
    [Fact]
    public void Build_OneBranchPerSource_AndFoundryReportUrlEvidence()
    {
        var items = Items(2);
        var local = Pass(items, "agenteval-local");
        var foundry = new AgentEvaluationResults("foundry", Pass(items, "foundry").Items, inputItems: items)
        {
            ReportUrl = new Uri("https://foundry.example/report/123"),
        };

        var report = UnifiedEvalReport.Build([("agenteval-local", local), ("foundry", foundry)]);

        Assert.NotNull(report.Details.SubResults);
        Assert.Equal(2, report.Details.SubResults!.Count);

        var foundryBranch = report.Details.SubResults!.Single(b => b.Metric.Name == "foundry");
        Assert.NotNull(foundryBranch.Details.Evidence);
        Assert.Contains(foundryBranch.Details.Evidence!, e => e.Reference == "report_url");
    }

    [Fact]
    public void Build_EmptyResultSet_IsAnErrorBranch_AndTheRootHasNoVerdict_NotAFail()
    {
        var items = Items(1);
        var empty = new AgentEvaluationResults(
            "foundry", new List<Microsoft.Extensions.AI.Evaluation.EvaluationResult>(), inputItems: items);   // no Error, zero items

        var report = UnifiedEvalReport.Build([("agenteval-local", Pass(items, "agenteval-local")), ("foundry", empty)]);

        var foundryBranch = report.Details.SubResults!.Single(b => b.Metric.Name == "foundry");
        Assert.Equal("error", foundryBranch.Score.Label);      // no verdict, not a confirmed fail
        Assert.Equal("none", foundryBranch.Score.Severity);
        // The root rests on both providers: one produced no verdict, so the root has none either — "error", severity none,
        // never a FAIL. It used to read PASS on the local branch alone (#203 review round 5, B10u: a pass resting on part
        // of what was asked). The local branch's pass stays visible beside it.
        Assert.Equal("error", report.Score.Label);
        Assert.Equal("none", report.Score.Severity);
        Assert.Equal("pass", report.Details.SubResults!.Single(b => b.Metric.Name == "agenteval-local").Score.Label);
    }

    private static AgentEvaluationResults FoundryWith(params (string Key, Microsoft.Extensions.AI.Evaluation.EvaluationMetric Metric)[][] perItem)
    {
        var results = perItem.Select(metrics =>
        {
            var r = new Microsoft.Extensions.AI.Evaluation.EvaluationResult();
            foreach (var (key, metric) in metrics)
                r.Metrics[key] = metric;
            return r;
        }).ToList();
        return new AgentEvaluationResults("foundry", results, inputItems: Items(perItem.Length));
    }

    [Fact]
    public void AnUnparseableMetric_BesideAPass_IsNoVerdict_NotAPass()
    {
        // Review round 5 H-1 (B10u): the bridge marks the metric "error", but Node() left error/skipped children out and
        // passed on the rest, so the Foundry branch and the root read PASS.
        var relevance = new Microsoft.Extensions.AI.Evaluation.NumericMetric("relevance", null);
        relevance.AddDiagnostics(Microsoft.Extensions.AI.Evaluation.EvaluationDiagnostic.Error("Failed to parse numeric score for 'relevance'."));
        var foundry = FoundryWith([("relevance", relevance), ("coherence", new Microsoft.Extensions.AI.Evaluation.NumericMetric("coherence", 4.5))]);

        var report = UnifiedEvalReport.Build([("foundry", foundry)]);

        var branch = Assert.Single(report.Details.SubResults!);
        Assert.Equal("error", branch.Score.Label);
        Assert.False(branch.Score.Passed);
        Assert.Equal("error", report.Score.Label);
        Assert.Equal(0.875, branch.Score.Value, 3);   // the measured metric only: (4.5 - 1) / 4
    }

    [Fact]
    public void AQueryThatWithheldItsPass_IsAWarn_NotAFailHigh()
    {
        // Review round 5 H-1, the other direction: passed = All(Passed) turned any warn child into FAIL / high.
        var foundry = FoundryWith(
            [("coherence", new Microsoft.Extensions.AI.Evaluation.NumericMetric("coherence", 4.5))],
            [("coherence", new Microsoft.Extensions.AI.Evaluation.NumericMetric("coherence", 4.5)),
             ("note", new Microsoft.Extensions.AI.Evaluation.StringMetric("note", "informational"))]);

        var report = UnifiedEvalReport.Build([("foundry", foundry)]);

        var branch = Assert.Single(report.Details.SubResults!);
        Assert.Equal("warn", branch.Score.Label);
        Assert.NotEqual("high", branch.Score.Severity);
        Assert.Equal("warn", report.Score.Label);
    }

    // What AgentEvalCompositeEvaluator emits for a passing composite with one failed (informational) leaf.
    private static (string, Microsoft.Extensions.AI.Evaluation.EvaluationMetric)[] PassingCompositeWithAFailedLeaf(string prefix = "") =>
    [
        ($"{prefix}Comp (overall)", new Microsoft.Extensions.AI.Evaluation.NumericMetric("Comp (overall)", 4.2,
            "AgentEval score: 80/100 (pass, severity none)")),
        ($"{prefix}b", new Microsoft.Extensions.AI.Evaluation.NumericMetric("b", 3.4,
            "AgentEval score: 60/100 (fail, severity medium)" + AgentEvalCompositeEvaluator.InformationalLeafNote)),
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void APassingComposite_ReadsPass_WithOneQueryOrTwo(int queries)
    {
        // Review round 6 M-4 (B10ac): with one query the flat branch re-rolled the promoted leaves and dropped the
        // "(overall)" verdict, so a passing composite read FAIL with 1 query and PASS with 2.
        var report = UnifiedEvalReport.Build([("agenteval-local",
            FoundryWith(Enumerable.Repeat(PassingCompositeWithAFailedLeaf(), queries).ToArray()))]);

        Assert.Equal("pass", report.Score.Label);
        Assert.Equal("pass", Assert.Single(report.Details.SubResults!).Score.Label);
    }

    [Fact]
    public async Task Build_WithComposite_KeepsRichLocalHierarchy()
    {
        // A depth-2 composite tree, captured by running the AgentEvalCompositeEvaluator once.
        var tree = Tree(Leaf("goal", 0.9), Leaf("rule", 0.8));
        var composite = new AgentEvalCompositeEvaluator(new StubComposite(tree));
        await composite.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "r")));   // populates CapturedResults

        var items = Items(1);
        var report = UnifiedEvalReport.Build([("agenteval-local", Pass(items, "agenteval-local"))], composite);

        var branch = Assert.Single(report.Details.SubResults!);
        // The spliced composite tree keeps its own children -> hierarchy depth > 1 (not flattened).
        Assert.NotNull(branch.Details.SubResults);
        Assert.All(branch.Details.SubResults!, node => Assert.NotEmpty(node.Details.SubResults!));
    }
}
