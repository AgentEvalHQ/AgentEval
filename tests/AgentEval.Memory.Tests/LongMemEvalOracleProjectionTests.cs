// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalOracleProjectionTests
{
    [Fact]
    public void Project_SingleSession_ClonesOnlyLabelledSessionAndStripsLabels()
    {
        var source = Entry(
            "q-single",
            ["distractor", "answer-session"],
            ["answer-session"],
            [Session("distractor", false), Session("answer evidence", true)],
            ["date-1", "date-2"]);

        var projected = LongMemEvalOracleProjector.Project(source).Entry;

        Assert.Equal("q-single", projected.QuestionId);
        var projectedSession = Assert.Single(projected.HaystackSessions!);
        Assert.Equal("answer evidence", projectedSession[0].Content);
        Assert.Null(projectedSession[0].HasAnswer);
        Assert.Equal(["date-2"], projected.HaystackDates);
        Assert.Empty(projected.HaystackSessionIds!);
        Assert.Empty(projected.AnswerSessionIds!);
        Assert.NotSame(source.HaystackSessions![1], projectedSession);
        Assert.True(source.HaystackSessions[1][0].HasAnswer);
    }

    [Fact]
    public void Project_MultiSession_PreservesSourceOrderingAndAlignedDates()
    {
        var source = Entry(
            "q-multi",
            ["answer-2", "distractor", "answer-1"],
            ["answer-1", "answer-2"],
            [
                Session("second answer chronologically", true),
                Session("distractor", false),
                Session("first answer by label order", true)
            ],
            ["date-a2", "date-d", "date-a1"]);

        var projected = LongMemEvalOracleProjector.Project(source).Entry;

        Assert.Equal(2, projected.HaystackSessions!.Count);
        Assert.Equal(
            ["second answer chronologically", "first answer by label order"],
            projected.HaystackSessions.Select(session => session[0].Content));
        Assert.Equal(["date-a2", "date-a1"], projected.HaystackDates);
        Assert.All(
            projected.HaystackSessions.SelectMany(session => session),
            turn => Assert.Null(turn.HasAnswer));
    }

    [Fact]
    public void Project_TemporalQuestion_PreservesQuestionDateAndSelectedSessionDates()
    {
        var source = Entry(
            "q-temporal",
            ["old", "new"],
            ["new"],
            [Session("old value", false), Session("new value", true)],
            ["2025/01/01", "2026/01/01"]);
        source.QuestionType = "temporal-reasoning";
        source.QuestionDate = "2026/02/01";

        var projected = LongMemEvalOracleProjector.Project(source).Entry;

        Assert.Equal("2026/02/01", projected.QuestionDate);
        Assert.Equal(["2026/01/01"], projected.HaystackDates);
        Assert.Equal("new value", Assert.Single(projected.HaystackSessions!)[0].Content);
    }

    [Fact]
    public void Project_AbstentionWithoutLabelledEvidence_HasEmptyHistory()
    {
        var source = Entry(
            "q-abstention_abs",
            ["distractor"],
            [],
            [Session("distractor", false)],
            ["date-1"]);

        var projected = LongMemEvalOracleProjector.Project(source).Entry;

        Assert.True(projected.IsAbstention);
        Assert.Empty(projected.HaystackSessions!);
        Assert.Empty(projected.HaystackDates!);
        Assert.Empty(projected.HaystackSessionIds!);
        Assert.Empty(projected.AnswerSessionIds!);
    }

    private static LongMemEvalEntry Entry(
        string questionId,
        List<string> sessionIds,
        List<string> answerSessionIds,
        List<List<LongMemEvalTurn>> sessions,
        List<string> dates) => new()
    {
        QuestionId = questionId,
        QuestionType = "single-session-user",
        Question = "What should be recalled?",
        AnswerRaw = JsonSerializer.SerializeToElement("gold answer"),
        HaystackSessionIds = sessionIds,
        AnswerSessionIds = answerSessionIds,
        HaystackSessions = sessions,
        HaystackDates = dates
    };

    private static List<LongMemEvalTurn> Session(string content, bool hasAnswer) =>
    [
        new LongMemEvalTurn
        {
            Role = "user",
            Content = content,
            HasAnswer = hasAnswer
        },
        new LongMemEvalTurn
        {
            Role = "assistant",
            Content = "ack",
            HasAnswer = false
        }
    ];
}
