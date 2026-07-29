// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalEvidenceSecurityTests
{
    [Fact]
    public void Capture_EnabledWithHostileDictionary_ReturnsOwnedFailureCode()
    {
        var response = new AgentResponse
        {
            Text = "answer",
            AdditionalProperties = new ThrowOnAccessDictionary("sensitive dictionary detail")
        };

        var result = Capture(response);

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.Invalid, result.Diagnostics!.Status);
        Assert.Equal("invalid_evidence", result.Diagnostics.SafeFailureCode);
        Assert.DoesNotContain("sensitive dictionary detail", JsonSerializer.Serialize(result));
    }

    [Theory]
    [MemberData(nameof(UnsupportedProviderObjects))]
    public void Capture_ArbitraryProviderObject_RejectsWithoutSerialization(object providerObject)
    {
        var result = Capture(ResponseWith(providerObject));

        Assert.Null(result.Envelope);
        Assert.Equal("invalid_evidence_schema", result.Diagnostics!.SafeFailureCode);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("provider secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", serialized, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<object> UnsupportedProviderObjects => new()
    {
        new InvalidOperationException("provider secret exception"),
        new Dictionary<string, string> { ["Authorization"] = "provider secret" },
        new { Embedding = new[] { 0.1, 0.2, 0.3 }, Secret = "provider secret" }
    };

    [Theory]
    [MemberData(nameof(HostileJson))]
    public void Capture_HostileJson_RejectsDeterministically(string json)
    {
        var result = Capture(ResponseWith(json));

        Assert.Null(result.Envelope);
        Assert.Equal(EvidenceObservationStatus.Invalid, result.Diagnostics!.Status);
        Assert.Equal("invalid_evidence_schema", result.Diagnostics.SafeFailureCode);
    }

    public static TheoryData<string> HostileJson => new()
    {
        """
        {"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Headers":{"Authorization":"secret"}}
        """,
        """
        {"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Embedding":[0.1,0.2]}
        """,
        """
        {"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Exception":{"Message":"secret"}}
        """,
        BuildDeepJson(),
        new string(' ', QuestionEvidenceEnvelope.MaximumSerializedLength + 1)
    };

    [Fact]
    public void Capture_ControlCharacterInIdentifier_RejectsDeterministically()
    {
        var envelope = new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved =
            [
                new EvidenceReference { Id = "unsafe\u0001id", Rank = 1 }
            ]
        };

        var result = Capture(ResponseWith(envelope));

        Assert.Null(result.Envelope);
        Assert.Equal("invalid_evidence_id", result.Diagnostics!.SafeFailureCode);
    }

    [Fact]
    public void Capture_CredentialLikeFullContent_RejectsDeterministically()
    {
        var envelope = new QuestionEvidenceEnvelope
        {
            SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
            Retrieved =
            [
                new EvidenceReference
                {
                    Id = "safe-id",
                    Rank = 1,
                    Content = "Authorization: Bearer provider secret"
                }
            ]
        };

        var result = Capture(ResponseWith(envelope), EvidenceCaptureMode.Full);

        Assert.Null(result.Envelope);
        Assert.Equal("sensitive_evidence_rejected", result.Diagnostics!.SafeFailureCode);
        Assert.DoesNotContain("provider secret", JsonSerializer.Serialize(result));
    }

    private static LongMemEvalEvidenceCapture.EvidenceCaptureResult Capture(
        AgentResponse response,
        EvidenceCaptureMode mode = EvidenceCaptureMode.References) =>
        LongMemEvalEvidenceCapture.Capture(
            response,
            new LongMemEvalEntry
            {
                QuestionId = "q-security",
                QuestionType = "single-session-user",
                Question = "Question?",
                HaystackSessions = [],
                HaystackDates = [],
                HaystackSessionIds = [],
                AnswerSessionIds = []
            },
            new ExternalBenchmarkOptions
            {
                EvidenceCaptureMode = mode,
                MaxJudgeRetries = 0
            });

    private static AgentResponse ResponseWith(object value) => new()
    {
        Text = "answer",
        AdditionalProperties = new Dictionary<string, object?>
        {
            [LongMemEvalEvidenceCapture.ReservedPropertyKey] = value
        }
    };

    private static string BuildDeepJson()
    {
        const int depth = 80;
        return
            """{"SchemaVersion":"1.0","Retrieved":[],"AnswerContext":[],"Nested":""" +
            string.Concat(Enumerable.Repeat("""{"Nested":""", depth)) +
            "null" +
            new string('}', depth + 1);
    }

    private sealed class ThrowOnAccessDictionary(string message)
        : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => throw new InvalidOperationException(message);
        public IEnumerable<string> Keys => throw new InvalidOperationException(message);
        public IEnumerable<object?> Values => throw new InvalidOperationException(message);
        public int Count => throw new InvalidOperationException(message);
        public bool ContainsKey(string key) => throw new InvalidOperationException(message);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            => throw new InvalidOperationException(message);
        public bool TryGetValue(string key, out object? value)
            => throw new InvalidOperationException(message);
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => throw new InvalidOperationException(message);
    }
}
