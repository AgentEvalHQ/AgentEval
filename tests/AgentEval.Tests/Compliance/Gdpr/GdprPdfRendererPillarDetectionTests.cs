// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Evals;
using Xunit;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// Regression coverage for BUG-03: the GDPR PDF renderer detected pillar pages via
/// Key.Contains("Pillar") OR Category.Contains("pillar"). Article composites have category
/// "compliance.{Pillar}" (which contains "pillar"), so in the flat Smoke preset every article
/// was rendered both as a mislabeled pillar page and an article page. Detection must key on the
/// node Key only.
/// </summary>
public class GdprPdfRendererPillarDetectionTests
{
    private static EvalResult Node(string key, string category) =>
        new(
            Metric: new(key, key, category, "1.0.0"),
            Score: new(1.0, null, "pass", true, 0.70, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("test", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("Pillar3-SubjectRights", "compliance.gdpr", true)]                 // real pillar grouping node
    [InlineData("gdpr.art17.erasure", "compliance.Pillar3-SubjectRights", false)]  // article — category has "Pillar" but must NOT match
    [InlineData("gdpr.art9.special_categories", "compliance.Pillar2-LawfulBasis", false)]
    public void IsPillarNode_IsKeyBased_NotCategoryBased(string key, string category, bool expected)
    {
        Assert.Equal(expected, GDPRPdfRenderer.IsPillarNode(Node(key, category)));
    }
}
