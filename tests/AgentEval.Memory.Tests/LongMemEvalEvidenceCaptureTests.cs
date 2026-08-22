// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalEvidenceCaptureTests
{
    [Fact]
    public void Capture_None_DoesNotInspectReservedProperty()
    {
        var response = AgentResponseWith(new ThrowOnAccessDictionary());
        var options = new ExternalBenchmarkOptions
        {
            EvidenceCaptureMode = EvidenceCaptureMode.None
        };

        var result = LongMemEvalEvidenceCapture.Capture(response, Entry(), options);

        Assert.Null(result.Envelope);
        Assert.Null(result.Diagnostics);
    }

    [Fact]
    public void Capture_References_CopiesAllowlistedDtoAndDropsNoOwnedFields()
    {
        var source = new List<EvidenceReference>
        {
            Reference("r-1", 1, "session-1", 0, similarity: 0.91)
        };
        var envelope = Envelope(source, []);
        var response = AgentResponseWith(new Dictionary<string, object?>
        {
            [LongMemEvalEvidenceCapture.ReservedPropertyKey] = envelope
        });

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));
        source.Clear();

        Assert.NotSame(envelope, result.Envelope);
        var captured = Assert.Single(result.Envelope!.Retrieved);
        Assert.Equal("r-1", captured.Id);
        Assert.Equal(0.91, captured.SimilarityScore);
        Assert.Null(captured.Content);
        Assert.Single(result.Envelope.Retrieved);
    }

    [Fact]
    public void Capture_UnrelatedAdditionalProperties_AreIgnored()
    {
        var response = AgentResponseWith(new Dictionary<string, object?>
        {
            ["provider.raw.request"] = new { Authorization = "Bearer secret" },
            ["anything.else"] = new InvalidOperationException("do not serialize")
        });

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.NotObserved, result.Diagnostics!.Status);
        Assert.Null(result.Diagnostics.SafeFailureCode);
    }

    [Fact]
    public void Capture_JsonWithUnknownField_FailsSafely()
    {
        const string json =
            """{"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Authorization":"secret"}""";
        var response = AgentResponseWith(new Dictionary<string, object?>
        {
            [LongMemEvalEvidenceCapture.ReservedPropertyKey] = json
        });

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.Invalid, result.Diagnostics!.Status);
        Assert.Equal("invalid_evidence_schema", result.Diagnostics.SafeFailureCode);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Capture_JsonWithAllowlistedFields_IsAccepted()
    {
        const string json =
            """
            {
              "SchemaVersion": "1.0",
              "Retrieved": [
                { "Id": "r-1", "Rank": 1, "SimilarityScore": 0.8, "SourceSessionId": "session-1", "SourceTurnIndex": 0 }
              ],
              "AnswerContext": []
            }
            """;
        var response = AgentResponseWith(new Dictionary<string, object?>
        {
            [LongMemEvalEvidenceCapture.ReservedPropertyKey] = json
        });

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Single(result.Envelope!.Retrieved);
        Assert.Equal(EvidenceObservationStatus.Observed, result.Diagnostics!.Status);
    }

    [Fact]
    public void Capture_ExcessiveReferenceCount_FailsSafely()
    {
        var references = Enumerable.Range(1, QuestionEvidenceEnvelope.MaximumReferences + 1)
            .Select(i => Reference($"r-{i}", i))
            .ToList();
        var response = AgentResponseWith(Reserved(Envelope(references, [])));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Null(result.Envelope);
        Assert.Equal("evidence_bounds_exceeded", result.Diagnostics!.SafeFailureCode);
    }

    [Theory]
    [MemberData(nameof(InvalidReferenceEnvelopes))]
    public void Capture_InvalidReference_FailsSafely(
        QuestionEvidenceEnvelope envelope,
        string expectedCode)
    {
        var response = AgentResponseWith(Reserved(envelope));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.Invalid, result.Diagnostics!.Status);
        Assert.Equal(expectedCode, result.Diagnostics.SafeFailureCode);
    }

    public static TheoryData<QuestionEvidenceEnvelope, string> InvalidReferenceEnvelopes => new()
    {
        {
            Envelope(
                [Reference("r-1", 1), Reference("r-2", 1)],
                []),
            "invalid_evidence_rank"
        },
        {
            Envelope(
                [Reference("r-1", 0)],
                []),
            "invalid_evidence_rank"
        },
        {
            Envelope(
                [Reference("r-1", 1, similarity: double.NaN)],
                []),
            "invalid_evidence_score"
        },
        {
            Envelope(
                [Reference("authorization: bearer secret", 1)],
                []),
            "sensitive_evidence_rejected"
        },
        {
            Envelope(
                [Reference("r-1", 1, content: "content not allowed")],
                []),
            "evidence_content_not_allowed"
        },
        {
            new QuestionEvidenceEnvelope
            {
                SchemaVersion = "2.0",
                Retrieved = [],
                AnswerContext = []
            },
            "unsupported_evidence_schema"
        }
    };

    [Fact]
    public void Capture_FullMode_AllowsBoundedContent()
    {
        var response = AgentResponseWith(Reserved(Envelope(
            [Reference("r-1", 1, content: "bounded evidence text")],
            [])));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.Full));

        Assert.Equal("bounded evidence text", Assert.Single(result.Envelope!.Retrieved).Content);
    }

    [Fact]
    public void Capture_FullMode_RejectsExcessiveContent()
    {
        var response = AgentResponseWith(Reserved(Envelope(
            [Reference("r-1", 1, content: new string('x', EvidenceReference.MaximumContentLength + 1))],
            [])));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.Full));

        Assert.Null(result.Envelope);
        Assert.Equal("evidence_bounds_exceeded", result.Diagnostics!.SafeFailureCode);
    }

    [Fact]
    public void Capture_MissingReservedEvidence_IsNotObservedNotFailure()
    {
        var result = LongMemEvalEvidenceCapture.Capture(
            new AgentResponse { Text = "answer" },
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.NotObserved, result.Diagnostics!.Status);
        Assert.Null(result.Diagnostics.GoldSessionPresent);
        Assert.Null(result.Diagnostics.HasAnswerTurnPresent);
    }

    [Fact]
    public void Capture_DerivesGoldDiagnosticsOnlyAtEvaluatorBoundary()
    {
        var envelope = Envelope(
            [
                Reference("r-1", 1, "session-other", 0),
                Reference("r-2", 2, "session-gold", 0),
                Reference("r-3", 3, "session-gold", 1)
            ],
            [
                Reference("c-1", 1, "session-gold", 0, contextOrder: 2),
                Reference("c-2", 2, "session-other", 0, contextOrder: 1)
            ]);
        var response = AgentResponseWith(Reserved(envelope));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References, topK: 2));

        var diagnostics = result.Diagnostics!;
        Assert.Equal(EvidenceObservationStatus.Observed, diagnostics.Status);
        Assert.True(diagnostics.GoldSessionPresent);
        Assert.True(diagnostics.HasAnswerTurnPresent);
        Assert.Equal(2, diagnostics.FirstGoldRank);
        Assert.Equal(2, diagnostics.DistinctSourceSessionCount);
        Assert.NotNull(diagnostics.SourceSessionDiversityRatio);
        Assert.Equal(2.0 / 3.0, diagnostics.SourceSessionDiversityRatio.Value, precision: 10);
        Assert.Equal([2, 1], diagnostics.AnswerContextOrders);
        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("session-gold", serialized);
        Assert.DoesNotContain("has_answer", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capture_SessionOnlyReferences_LeaveTurnRecallNotObserved()
    {
        var response = AgentResponseWith(Reserved(Envelope(
            [Reference("r-1", 1, "session-gold")],
            [])));

        var result = LongMemEvalEvidenceCapture.Capture(
            response,
            Entry(),
            Options(EvidenceCaptureMode.References));

        Assert.True(result.Diagnostics!.GoldSessionPresent);
        Assert.Null(result.Diagnostics.HasAnswerTurnPresent);
    }

    [Fact]
    public void Capture_CountsRequiredEvidenceSeparatelyAtEachBoundary()
    {
        // The failure this exists to make visible: retrieval ranks EVERY required session in the
        // top four, and a downstream context budget then supplies exactly one of them to the model.
        // A consumer reading the any-checks sees a healthy retrieval and has to infer the rest from
        // which way the answers are wrong.
        var envelope = Envelope(
            [
                Reference("r-1", 1, "session-gold-a", 0),
                Reference("r-2", 2, "session-gold-b", 0),
                Reference("r-3", 3, "session-gold-c", 0),
                Reference("r-4", 4, "session-gold-d", 0)
            ],
            [Reference("c-1", 1, "session-gold-a", 0, contextOrder: 1)]);

        var diagnostics = LongMemEvalEvidenceCapture.Capture(
            AgentResponseWith(Reserved(envelope)),
            MultiGoldEntry(),
            Options(EvidenceCaptureMode.References, topK: 4)).Diagnostics!;

        Assert.Equal(4, diagnostics.RequiredEvidenceSessionCount);
        Assert.Equal(4, diagnostics.RequiredEvidenceSessionsRetrieved);
        Assert.Equal(1, diagnostics.RequiredEvidenceSessionsInAnswerContext);

        // The old instrument, on the same evidence, reporting nothing wrong. Asserted rather than
        // described so that any future attempt to "fix" the any-check has to confront the fact that
        // it is answering a different question, not a broken version of this one.
        Assert.True(diagnostics.GoldSessionPresent);
        Assert.Equal(1, diagnostics.FirstGoldRank);

        Assert.DoesNotContain("session-gold", JsonSerializer.Serialize(diagnostics));
    }

    [Fact]
    public void Capture_UninstrumentedAnswerContext_LeavesRequiredCoverageNotMeasured()
    {
        // Answer-context references carrying no session ID. Zero required sessions are OBSERVED in
        // the context, and reporting that as zero would be indistinguishable from a budget that
        // dropped all of them -- the precise confusion this field exists to end.
        var envelope = Envelope(
            [Reference("r-1", 1, "session-gold-a", 0), Reference("r-2", 2, "session-gold-b", 0)],
            [Reference("c-1", 1, contextOrder: 1), Reference("c-2", 2, contextOrder: 2)]);

        var diagnostics = LongMemEvalEvidenceCapture.Capture(
            AgentResponseWith(Reserved(envelope)),
            MultiGoldEntry(),
            Options(EvidenceCaptureMode.References, topK: 4)).Diagnostics!;

        Assert.Equal(2, diagnostics.RequiredEvidenceSessionsRetrieved);
        Assert.Null(diagnostics.RequiredEvidenceSessionsInAnswerContext);
        Assert.Equal(2, diagnostics.AnswerContextReferenceCount);
    }

    private static LongMemEvalEntry MultiGoldEntry() => new()
    {
        QuestionId = "q-multi",
        QuestionType = "multi-session",
        Question = "How many orders?",
        HaystackSessionIds =
            ["session-gold-a", "session-gold-b", "session-gold-c", "session-gold-d", "session-other"],
        AnswerSessionIds = ["session-gold-a", "session-gold-b", "session-gold-c", "session-gold-d"],
        HaystackDates =
        [
            "2026/01/01 (Thu) 00:00", "2026/01/02 (Fri) 00:00", "2026/01/03 (Sat) 00:00",
            "2026/01/04 (Sun) 00:00", "2026/01/05 (Mon) 00:00"
        ],
        HaystackSessions =
        [
            [new LongMemEvalTurn { Role = "user", Content = "a", HasAnswer = true }],
            [new LongMemEvalTurn { Role = "user", Content = "b", HasAnswer = true }],
            [new LongMemEvalTurn { Role = "user", Content = "c", HasAnswer = true }],
            [new LongMemEvalTurn { Role = "user", Content = "d", HasAnswer = true }],
            [new LongMemEvalTurn { Role = "user", Content = "other", HasAnswer = false }]
        ]
    };

    private static ExternalBenchmarkOptions Options(
        EvidenceCaptureMode mode,
        int topK = 10) => new()
    {
        EvidenceCaptureMode = mode,
        EvidenceTopK = topK,
        MaxJudgeRetries = 0
    };

    private static AgentResponse AgentResponseWith(
        IReadOnlyDictionary<string, object?> properties) => new()
    {
        Text = "answer",
        AdditionalProperties = properties
    };

    private static Dictionary<string, object?> Reserved(object value) => new()
    {
        [LongMemEvalEvidenceCapture.ReservedPropertyKey] = value
    };

    private static QuestionEvidenceEnvelope Envelope(
        IReadOnlyList<EvidenceReference> retrieved,
        IReadOnlyList<EvidenceReference> answerContext) => new()
    {
        SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
        Retrieved = retrieved,
        AnswerContext = answerContext
    };

    private static EvidenceReference Reference(
        string id,
        int rank,
        string? sourceSessionId = null,
        int? sourceTurnIndex = null,
        double? similarity = null,
        int? contextOrder = null,
        string? content = null) => new()
    {
        Id = id,
        Rank = rank,
        SourceSessionId = sourceSessionId,
        SourceTurnIndex = sourceTurnIndex,
        SimilarityScore = similarity,
        AnswerContextOrder = contextOrder,
        Content = content
    };

    private static LongMemEvalEntry Entry() => new()
    {
        QuestionId = "q-1",
        QuestionType = "single-session-user",
        Question = "What happened?",
        HaystackSessionIds = ["session-gold", "session-other"],
        AnswerSessionIds = ["session-gold"],
        HaystackDates = ["2026/01/01 (Thu) 00:00", "2026/01/02 (Fri) 00:00"],
        HaystackSessions =
        [
            [
                new LongMemEvalTurn { Role = "user", Content = "gold", HasAnswer = true },
                new LongMemEvalTurn { Role = "assistant", Content = "other", HasAnswer = false }
            ],
            [
                new LongMemEvalTurn { Role = "user", Content = "other", HasAnswer = false }
            ]
        ]
    };

    private sealed class ThrowOnAccessDictionary : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException("must not inspect");
        public IEnumerable<string> Keys => throw new InvalidOperationException("must not inspect");
        public IEnumerable<object?> Values => throw new InvalidOperationException("must not inspect");
        public int Count => throw new InvalidOperationException("must not inspect");
        public bool ContainsKey(string key) => throw new InvalidOperationException("must not inspect");
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => throw new InvalidOperationException("must not inspect");
        public bool TryGetValue(string key, out object? value)
            => throw new InvalidOperationException("must not inspect");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => throw new InvalidOperationException("must not inspect");
    }
}
