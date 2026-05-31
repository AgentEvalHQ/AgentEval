// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.EuAiAct;

/// <summary>
/// Regression coverage for BUG-03 (EU AI Act side): pillar detection must key on the node Key,
/// not the category (article composites carry category "compliance.{Pillar}").
/// </summary>
public class EuAiActPdfRendererPillarDetectionTests
{
    private static EvalResult Node(string key, string category) =>
        new(
            Metric: new(key, key, category, "1.0.0"),
            Score: new(1.0, null, "pass", true, 0.70, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("test", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("Pillar1-ProhibitedPractices", "compliance.eu_ai", true)]              // real pillar grouping node
    [InlineData("eu_ai.art5.subliminal", "compliance.Pillar1-ProhibitedPractices", false)] // article node
    [InlineData("eu_ai.art13.transparency", "compliance.Pillar2-TransparencyToPersons", false)]
    public void IsPillarNode_IsKeyBased_NotCategoryBased(string key, string category, bool expected)
    {
        Assert.Equal(expected, EuAiActPdfRenderer.IsPillarNode(Node(key, category)));
    }
}
