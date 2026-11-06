// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// summary.md, the file a tester reads first, groups the issues by who can fix them: a failed judge call is listed under
/// its own Judge heading (the judge's provider settings), never under the sign-in or Work IQ, whatever its category.
/// </summary>
public sealed class SummaryTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task AJudge401_IsListedUnderJudge_NotUnderTheSignIn()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new ThrowingJudgeChatClient();
        var dataset = _rig.WriteDataset("""
            [
              { "id": "case_ok", "input": "In one sentence, what is on my calendar?", "evaluation_criteria": ["Answers in one sentence"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(11);
        var summary = run.Text("summary.md");
        Assert.Contains("### Judge (1) — the judge model's provider settings (AI_INFERENCE_PROVIDER and its variables), not the agent or Work IQ", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("### Authentication", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("the sign-in (you, or the app registration)", summary, StringComparison.Ordinal);

        // The entry keeps its code, its HTTP status, and the judge's cause and action.
        Assert.Contains("- **judge.call_failed** at stage Outcome — HTTP 401", summary, StringComparison.Ordinal);
        Assert.Contains("  - cause: The judge model's provider refused the judge's key or deployment (HTTP 401)", summary, StringComparison.Ordinal);
        Assert.Contains("  - action: Check AI_INFERENCE_PROVIDER and that provider's API key", summary, StringComparison.Ordinal);

        // The final table groups it the same way, and issues.jsonl keeps the issue's own category.
        Assert.Contains(run.OutLines, l => l.TrimStart().StartsWith("Issues: ", StringComparison.Ordinal) && l.Contains("Judge 1", StringComparison.Ordinal)
                                           && !l.Contains("Authentication", StringComparison.Ordinal));
        var issue = Assert.Single(run.JsonLines("issues.jsonl"), i => i.GetProperty("code").GetString() == "judge.call_failed");
        Assert.Equal("Authentication", issue.GetProperty("category").GetString());
    }
}
