// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
