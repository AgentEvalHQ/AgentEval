// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// No dataset field is dropped without a word: a field the demo does not run (<c>expected</c>, or one it does not read,
/// such as a misspelt <c>evaluation_criteria</c> or a field of another dataset format) is ignored, and the banner and
/// summary.md name it per case. A file that does not fit the format stays a usage error.
/// </summary>
public sealed class DatasetTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task AFieldTheDemoDoesNotRead_IsIgnored_AndTheBannerAndTheSummaryNameTheCaseAndTheField()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();
        // A misspelt criteria field runs the case with no criteria, so the run must say which field it ignored, and where.
        var dataset = _rig.WriteDataset("""
            [
              { "id": "case_a", "input": "In one sentence, what is A?", "evaluation_criterion": ["Answers in one sentence"] },
              { "id": "case_b", "input": "In one sentence, what is B?", "evaluation_criteria": ["Answers in one sentence"], "metadata": { "owner": "x" } }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(0);   // nothing is refused: case_a runs with no criteria (its criteria check is not run), case_b is graded
        foreach (var note in new[] { "'evaluation_criterion' is ignored in 1 case(s) (case_a)", "'metadata' is ignored in 1 case(s) (case_b)" })
        {
            Assert.Contains(note, run.Out, StringComparison.Ordinal);
            Assert.Contains(note, run.Text("summary.md"), StringComparison.Ordinal);
        }

        Assert.Contains("the demo does not read it", run.Text("summary.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpectedField_IsAcceptedAndIgnored_AndTheBannerAndTheSummarySaySo()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();
        var dataset = _rig.WriteDataset("""
            [
              { "id": "case_a", "input": "In one sentence, what is A?", "expected": "A", "evaluation_criteria": ["Answers in one sentence"] },
              { "id": "case_b", "input": "In one sentence, what is B?", "evaluation_criteria": ["Answers in one sentence"] }
            ]
            """);

        var run = await _rig.RunAsync(DemoRig.Consent, "--dataset", dataset.FullName);

        run.AssertExit(0);
        const string note = "'expected' is ignored in 1 case(s) (case_a)";
        Assert.Contains(note, run.Out, StringComparison.Ordinal);
        Assert.Contains(note, run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains("substring check", run.Text("summary.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedDefault_HasTheFiveCases_AndNoIgnoredField()
    {
        var set = DemoDataset.LoadDefault();

        Assert.Equal(DemoRig.DefaultCaseIds, set.Cases.Select(c => c.Id).ToArray());
        Assert.Null(set.IgnoredFieldsNote);
        Assert.All(set.Cases, c => Assert.NotEmpty(c.Criteria));
    }
}
