// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// The turns AgentEval synthesises when injecting history, and how a caller suppresses or identifies
/// them.
/// </summary>
/// <remarks>
/// A memory system ingests injected history and retrieves from it. Scaffolding AgentEval added is
/// indistinguishable from dataset content once retrieved, so a retrieval set that is entirely
/// scaffolding reads as a defect in the system under test rather than as an artefact of the harness.
/// </remarks>
public class LongMemEvalSyntheticTurnTests
{
    private static LongMemEvalEntry Entry() => new()
    {
        QuestionId = "q-1",
        QuestionType = "multi-session",
        Question = "What did the user say?",
        AnswerRaw = JsonSerializer.SerializeToElement("gold"),
        QuestionDate = "2026/01/02 (Fri) 10:00",
        HaystackDates = ["2026/01/01 (Thu) 09:00", "2026/01/02 (Fri) 09:00"],
        HaystackSessions =
        [
            [
                new LongMemEvalTurn { Role = "user", Content = "REAL USER ONE" },
                new LongMemEvalTurn { Role = "assistant", Content = "REAL ASSISTANT ONE" }
            ],
            [
                // A user turn with no assistant reply — the case that produces filler.
                new LongMemEvalTurn { Role = "user", Content = "REAL USER TWO" }
            ]
        ]
    };

    // ── Already true in 0.19: the boundary pair can be suppressed ─────────────

    [Fact]
    public void Default_EmitsTheSessionBoundaryPairVerbatim()
    {
        var turns = LongMemEvalHistoryFormatter.Format(Entry(), new ExternalBenchmarkOptions());

        Assert.Contains(turns, t =>
            t.UserMessage.StartsWith(LongMemEvalHistoryFormatter.SessionMarkerPrefix, StringComparison.Ordinal));
        Assert.Contains(turns, t =>
            t.AssistantResponse == LongMemEvalHistoryFormatter.SessionBoundaryAcknowledgement);
        Assert.Equal("--- Session 1 (2026/01/01 (Thu) 09:00) ---", turns[0].UserMessage);
        Assert.Equal("Understood. Starting a new conversation session.", turns[0].AssistantResponse);
    }

    [Fact]
    public void PreserveSessionBoundariesFalse_RemovesTheBoundaryPairEntirely()
    {
        // This option already existed in 0.19 (ExternalBenchmarkOptions.PreserveSessionBoundaries)
        // and already does exactly this. Pinned so the behaviour cannot regress unnoticed.
        var turns = LongMemEvalHistoryFormatter.Format(
            Entry(), new ExternalBenchmarkOptions { PreserveSessionBoundaries = false });

        Assert.DoesNotContain(turns, t =>
            t.UserMessage.StartsWith(LongMemEvalHistoryFormatter.SessionMarkerPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(turns, t =>
            t.AssistantResponse == LongMemEvalHistoryFormatter.SessionBoundaryAcknowledgement);
        // Real dataset content survives untouched.
        Assert.Equal("REAL USER ONE", turns[0].UserMessage);
        Assert.Equal("REAL ASSISTANT ONE", turns[0].AssistantResponse);
    }

    /// <summary>
    /// The gap that flag leaves: the filler reply for an unpaired user turn is synthetic too, and
    /// suppressing boundaries does not suppress it.
    /// </summary>
    [Fact]
    public void PreserveSessionBoundariesFalse_StillEmitsTheUnpairedUserFiller()
    {
        var turns = LongMemEvalHistoryFormatter.Format(
            Entry(), new ExternalBenchmarkOptions { PreserveSessionBoundaries = false });

        Assert.Contains(turns, t =>
            t.AssistantResponse == LongMemEvalHistoryFormatter.UnpairedUserAcknowledgement);
    }

    /// <summary>
    /// The second gap, and the sharper one: <c>PreserveSessionBoundaries</c> is read only by
    /// structured injection, while <c>HistoryInjectionMode</c> defaults to <c>TextBlob</c>. On
    /// otherwise-default options, setting it to false therefore changes nothing at all.
    /// </summary>
    /// <remarks>
    /// Characterization, not aspiration: the text blob is the official LongMemEval prompt format, and
    /// stripping its <c>### Session N:</c> headers would change what the paper methodology sends to
    /// the model. The behaviour is correct and the silence about it was not.
    /// </remarks>
    [Fact]
    public void PreserveSessionBoundaries_IsIgnoredByTheTextBlobFormat()
    {
        var withBoundaries = LongMemEvalHistoryFormatter.FormatAsTextBlob(
            Entry(), new ExternalBenchmarkOptions { PreserveSessionBoundaries = true });
        var withoutBoundaries = LongMemEvalHistoryFormatter.FormatAsTextBlob(
            Entry(), new ExternalBenchmarkOptions { PreserveSessionBoundaries = false });

        Assert.Equal(withBoundaries, withoutBoundaries);
        Assert.Contains("### Session 1:", withoutBoundaries);
        // And TextBlob is what a default run uses, so the option is inert unless injection mode changes.
        Assert.Equal(HistoryInjectionMode.TextBlob, new ExternalBenchmarkOptions().HistoryInjectionMode);
    }

    // ── New in this release: every synthetic turn can be marked ───────────────

    [Fact]
    public void SyntheticTurnMarker_PrefixesEverySynthesisedTurnAndNothingElse()
    {
        const string Marker = "[[AE-SYNTHETIC]]";

        var turns = LongMemEvalHistoryFormatter.Format(
            Entry(), new ExternalBenchmarkOptions { SyntheticTurnMarker = Marker });

        var synthetic = turns
            .SelectMany(t => new[] { t.UserMessage, t.AssistantResponse })
            .Where(text => text.StartsWith(Marker, StringComparison.Ordinal))
            .ToList();

        // Boundary user marker + boundary ack, twice (two sessions), plus one unpaired-user filler.
        Assert.Equal(5, synthetic.Count);
        Assert.Contains(Marker + "--- Session 1 (2026/01/01 (Thu) 09:00) ---", synthetic);
        Assert.Contains(Marker + LongMemEvalHistoryFormatter.SessionBoundaryAcknowledgement, synthetic);
        Assert.Contains(Marker + LongMemEvalHistoryFormatter.UnpairedUserAcknowledgement, synthetic);

        // Dataset content is never marked, so removing marked turns removes only scaffolding.
        Assert.Contains(turns, t => t.UserMessage == "REAL USER ONE");
        Assert.Contains(turns, t => t.AssistantResponse == "REAL ASSISTANT ONE");
        Assert.Contains(turns, t => t.UserMessage == "REAL USER TWO");
    }

    [Fact]
    public void SyntheticTurnMarker_LeavesRealContentReachableAfterFiltering()
    {
        const string Marker = "[[AE]]";

        var turns = LongMemEvalHistoryFormatter.Format(
            Entry(), new ExternalBenchmarkOptions { SyntheticTurnMarker = Marker });

        var realOnly = turns
            .Where(t => !t.UserMessage.StartsWith(Marker, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, realOnly.Count);
        Assert.Equal("REAL USER ONE", realOnly[0].UserMessage);
        Assert.Equal("REAL USER TWO", realOnly[1].UserMessage);
    }

    [Fact]
    public void SyntheticTurnMarker_Null_IsByteIdenticalToTodaysOutput()
    {
        var withoutOption = LongMemEvalHistoryFormatter.Format(Entry(), new ExternalBenchmarkOptions());
        var withNullMarker = LongMemEvalHistoryFormatter.Format(
            Entry(), new ExternalBenchmarkOptions { SyntheticTurnMarker = null });

        Assert.Equal(withoutOption, withNullMarker);
        // And the historical literals are still exactly what they were.
        Assert.Equal("Understood. Starting a new conversation session.", withoutOption[0].AssistantResponse);
        Assert.Equal("I understand.", withoutOption[^1].AssistantResponse);
    }

    [Fact]
    public void SyntheticTurnMarker_DoesNotAlterTheOfficialTextBlobPrompt()
    {
        // The text blob is the official LongMemEval prompt format; marking it would change what the
        // paper methodology sends to the model.
        var unmarked = LongMemEvalHistoryFormatter.FormatAsTextBlob(Entry(), new ExternalBenchmarkOptions());
        var marked = LongMemEvalHistoryFormatter.FormatAsTextBlob(
            Entry(), new ExternalBenchmarkOptions { SyntheticTurnMarker = "[[AE]]" });

        Assert.Equal(unmarked, marked);
        Assert.DoesNotContain("[[AE]]", marked);
    }

    [Fact]
    public void SyntheticTurnMarker_IsLengthBounded()
    {
        var options = new ExternalBenchmarkOptions
        {
            SyntheticTurnMarker = new string('x', ExternalBenchmarkOptions.MaximumSyntheticTurnMarkerLength + 1)
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
