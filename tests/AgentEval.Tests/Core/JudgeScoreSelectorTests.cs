// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Tests.Evals;
using Xunit;

namespace AgentEval.Tests.Core;

public sealed class JudgeScoreSelectorTests
{
    private static readonly JudgeCriterion[] Declared = [new("a", "Korrekt"), new("b", "Tydligt"), new("c", "Med källor")];

    [Fact]
    public void Select_TwoOfThree_PreservesExactScoreAndRawVerdict()
    {
        var verdict = Verdict([Result("c", false), Result("a", true), Result("b", true)]);
        var selection = Select(verdict);
        Assert.Equal(100.0 * 2 / 3, selection.SelectedScore);
        Assert.Equal(90, selection.RawOverallScore);
        Assert.Equal(3, selection.ExpectedCount);
        Assert.Equal(3, selection.ApplicableCount);
        Assert.Equal(2, selection.MetCount);
        Assert.Equal(90, verdict.OverallScore);
        Assert.Equal(90, JudgeScoreSelector.Select(JudgeScorePolicy.JudgeOverall, verdict, []).SelectedScore);
    }

    [Theory]
    [InlineData("missing", "missing-criterion")]
    [InlineData("duplicate", "duplicate-criterion")]
    [InlineData("unknown", "unknown-criterion")]
    [InlineData("case", "unknown-criterion")]
    [InlineData("null-id", "unknown-criterion")]
    [InlineData("unmeasured", "unmeasured-criterion")]
    [InlineData("invalid-set", "invalid-verdict-set")]
    [InlineData("judge-failed", "judge-failed")]
    [InlineData("none-applicable", "no-applicable-criteria")]
    public void Select_IncompleteOrAmbiguousVerdicts_IsUnmeasured(string kind, string reason)
    {
        var results = new List<CriterionResult> { Result("a", true), Result("b", true), Result("c", true) };
        if (kind == "missing") results.RemoveAt(2);
        if (kind == "duplicate") results.Add(Result("c", false));
        if (kind == "unknown") results[2] = Result("d", true);
        if (kind == "case") results[2] = Result("C", true);
        if (kind == "null-id") results[2] = Result(null, true);
        if (kind == "unmeasured") results[2] = new() { Id = "c", IsMeasured = false };
        if (kind == "none-applicable") results = Declared.Select(c => new CriterionResult { Id = c.Id, Applicable = false }).ToList();
        var verdict = new EvaluationResult { OverallScore = 90, CriteriaResults = results,
            EvaluationFailed = kind == "judge-failed", CriteriaResultsValid = kind != "invalid-set" };
        var selection = Select(verdict);
        Assert.Null(selection.SelectedScore);
        Assert.Equal(reason, selection.FailureCode);
    }

    [Fact]
    public void Select_ExplicitInapplicability_UsesApplicableDenominator()
    {
        var selection = Select(Verdict([Result("a", true), Result("b", false), new() { Id = "c", Applicable = false }]));
        Assert.Equal(50, selection.SelectedScore);
        Assert.Equal(2, selection.ApplicableCount);
    }

    [Fact]
    public void Select_LegacyText_OnlyUnrendersDeclaredOrdinals()
    {
        var selection = JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction,
            Verdict([new() { Criterion = "1. Re-check the sources", Met = true }, new() { Criterion = "2. Be concise", Met = false }]),
            ["Re-check the sources", "Be concise"]);
        Assert.Equal(50, selection.SelectedScore);
        Assert.Equal("ambiguous-criteria", JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, Verdict([]), ["a", "A"]).FailureCode);
        Assert.Equal("no-criteria", JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, Verdict([]), []).FailureCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public async Task Evaluate_InvalidMetBoolean_DoesNotBecomeAFalseMeasurement(string met)
    {
        using var client = new RubricJudgeTests.RecordingChatClient(_ =>
            "{\"overallScore\":90,\"criteriaResults\":[{\"id\":\"a\",\"met\":" + met + "}]}");
        var verdict = await new ChatClientEvaluator(client).EvaluateAsync(EvaluationRequest.ForCriteria("q", "a", [Declared[0]]));
        Assert.False(Assert.Single(verdict.CriteriaResults).IsMeasured);
        Assert.Null(JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, verdict, ["Korrekt"], [Declared[0]]).SelectedScore);
    }

    [Theory]
    [InlineData("{\"id\":\"a\"}")]
    [InlineData("{\"id\":\"a\",\"met\":true,\"met\":false}")]
    [InlineData("{\"id\":\"a\",\"met\":true,\"applicable\":\"false\"}")]
    public void Parse_MissingOrConflictingBooleans_IsUnmeasured(string criterion)
    {
        var parsed = ChatClientEvaluator.ParseEvaluationResponse("{\"overallScore\":90,\"criteriaResults\":[" + criterion + "]}");
        Assert.False(Assert.Single(parsed.CriteriaResults).IsMeasured);
    }

    [Fact]
    public void Parse_ConflictingVerdictObjects_CannotSelectCriteriaFromFirst()
    {
        var parsed = ChatClientEvaluator.ParseEvaluationResponse("""
            {"overallScore":90,"criteriaResults":[{"id":"a","met":true}]}
            {"overallScore":90,"criteriaResults":[{"id":"a","met":false}]}
            """);
        Assert.False(parsed.CriteriaResultsValid);
        Assert.Equal(90, parsed.OverallScore);
    }

    [Fact]
    public void ForCriteria_CopiesListsAndRejectsDuplicateIds()
    {
        var criteria = Declared.ToList();
        var request = EvaluationRequest.ForCriteria("q", "a", criteria);
        criteria.Clear();
        Assert.Equal(3, request.JudgeCriteria!.Count);
        Assert.Throws<ArgumentException>(() => EvaluationRequest.ForCriteria("q", "a", [Declared[0], Declared[0]]));
        Assert.Throws<ArgumentException>(() => new JudgeCriterion("sv text", "text"));
        Assert.Throws<ArgumentException>(() => new JudgeCriterion("valid", " "));
    }

    [Fact]
    public void BindInstrument_SelectionChangesKnownIdentity_UnknownPromptStaysUnknown()
    {
        Assert.Equal("sha256:known", JudgeScoreSelector.BindInstrument("sha256:known", JudgeScorePolicy.JudgeOverall));
        Assert.NotEqual("sha256:known", JudgeScoreSelector.BindInstrument("sha256:known", JudgeScorePolicy.CriteriaMetFraction));
        Assert.Null(JudgeScoreSelector.BindInstrument(null, JudgeScorePolicy.CriteriaMetFraction));
        JudgeScoreSelection.RequireCompatible([null, new() { PolicyId = JudgeScoreSelection.OverallPolicyId }]);
        Assert.Throws<InvalidOperationException>(() => JudgeScoreSelection.RequireCompatible([null, Select(Verdict([]))]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Select_UnavailableOrOutOfRangeOverallScore_IsUnmeasured(int? score)
    {
        var selected = JudgeScoreSelector.Select(JudgeScorePolicy.JudgeOverall, new() { OverallScore = score }, []);
        Assert.Null(selected.SelectedScore);
        Assert.Equal("overall-score-unavailable", selected.FailureCode);
    }

    [Fact]
    public void Select_InvalidDeclarationsOrNullVerdictEntries_RefusesAmbiguity()
    {
        Assert.Equal("ambiguous-criteria", JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, Verdict([]), ["a", "a"]).FailureCode);
        Assert.Equal("ambiguous-criteria", JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, Verdict([]), [" "]).FailureCode);
        Assert.Equal("invalid-criterion-verdict", Select(Verdict([Result("a", true), Result("b", true), Result("c", true), null!])).FailureCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => JudgeScoreSelector.Select((JudgeScorePolicy)99, Verdict([]), []));
        Assert.Throws<ArgumentOutOfRangeException>(() => JudgeScoreSelector.BindInstrument(null, (JudgeScorePolicy)99));
    }

    [Fact]
    public async Task Evaluate_LegacyEvaluatorWithStableIds_RefusesToDiscardIdentity()
    {
        var legacy = new LegacyJudge();
        var verdict = await ((IEvaluator)legacy).EvaluateAsync(EvaluationRequest.ForCriteria("q", "a", Declared));
        Assert.True(verdict.EvaluationFailed);
        Assert.Equal(0, legacy.Calls);
    }

    private sealed class LegacyJudge : IEvaluator
    {
        public int Calls { get; private set; }
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationResult { OverallScore = 100 });
        }
    }

    private static JudgeScoreSelection Select(EvaluationResult verdict) =>
        JudgeScoreSelector.Select(JudgeScorePolicy.CriteriaMetFraction, verdict, Declared.Select(c => c.Text).ToArray(), Declared);
    private static EvaluationResult Verdict(IReadOnlyList<CriterionResult> results) => new() { OverallScore = 90, CriteriaResults = results };
    private static CriterionResult Result(string? id, bool met) => new() { Id = id, Criterion = "Translated wording may differ", Met = met };
}
