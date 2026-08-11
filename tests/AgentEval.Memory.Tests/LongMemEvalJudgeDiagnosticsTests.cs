// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using static AgentEval.Memory.Tests.LongMemEvalStructuredJudgeTests;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Tests for retaining judge diagnostics independently of how much evidence is rendered, and for the
/// judge's sampling determinism.
/// </summary>
public class LongMemEvalJudgeDiagnosticsTests
{
    private const string JudgeText = "yes because it matches the gold answer";

    [Theory]
    [InlineData(JudgeEvidenceMode.None)]
    [InlineData(JudgeEvidenceMode.Outcome)]
    [InlineData(JudgeEvidenceMode.Explanation)]
    public async Task RetainRawJudgeResponse_PopulatesRawOutsideRawEvidenceMode(JudgeEvidenceMode mode)
    {
        var judge = CreateJudge(Response(JudgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = mode,
            RetainRawJudgeResponse = true
        });

        Assert.Equal(JudgeText, result.RawResponse);
    }

    [Theory]
    [InlineData(JudgeEvidenceMode.None, null)]
    [InlineData(JudgeEvidenceMode.Outcome, "Judge outcome: Yes")]
    [InlineData(JudgeEvidenceMode.Explanation, "Judge said: " + JudgeText)]
    public async Task RetainRawJudgeResponse_DoesNotChangeTheRenderedExplanation(
        JudgeEvidenceMode mode,
        string? expectedExplanation)
    {
        var judge = CreateJudge(Response(JudgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = mode,
            RetainRawJudgeResponse = true
        });

        // The whole point of the option: keep the raw text without changing what is rendered.
        Assert.Equal(expectedExplanation, result.Explanation);
    }

    [Theory]
    [InlineData(JudgeEvidenceMode.None)]
    [InlineData(JudgeEvidenceMode.Outcome)]
    [InlineData(JudgeEvidenceMode.Explanation)]
    public async Task RetainRawJudgeResponse_DefaultOff_LeavesRawResponseNull(JudgeEvidenceMode mode)
    {
        var judge = CreateJudge(Response(JudgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = mode
        });

        Assert.Null(result.RawResponse);
    }

    /// <summary>
    /// Pins the serialized shape of a default-options judgment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// v0.20.0-beta adds three always-present integers — PrimaryLlmCallCount, RetryLlmCallCount and
    /// AttemptsUsed — so this shape is NOT identical to v0.19.0-beta's. The addition is deliberate
    /// and additive: a call-accounting field that only appears when opted in is useless to a validity
    /// gate that has to run on every result. System.Text.Json ignores unknown properties on
    /// deserialization, so a consumer reading these results keeps working; a consumer asserting an
    /// exact property set does not, which is why the change is called out rather than absorbed.
    /// </para>
    /// <para>
    /// Everything else added in this release stays WhenWritingNull or opt-in and is absent here.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DefaultOptions_SerializeToThePinnedShape()
    {
        var judge = CreateJudge(Response(JudgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.Outcome
        });

        var json = JsonSerializer.Serialize(result);

        Assert.Equal(
            """
            {"Status":0,"Correct":true,"RawScore":100,"Explanation":"Judge outcome: Yes","TokensUsed":0,"LlmCallCount":1,"AttemptCount":1,"PrimaryLlmCallCount":1,"RetryLlmCallCount":0,"AttemptsUsed":1,"SafeFailureCode":null}
            """,
            json);
    }

    /// <summary>
    /// The v0.19.0-beta property set must still be present, unchanged in name and value. This is the
    /// half of the previous guarantee that still holds and still matters: the addition is additive,
    /// and nothing a 0.19 consumer already reads has moved or changed meaning.
    /// </summary>
    [Fact]
    public async Task DefaultOptions_PreserveEveryPropertyV019Emitted()
    {
        var judge = CreateJudge(Response(JudgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.Outcome
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("Status").GetInt32());
        Assert.True(root.GetProperty("Correct").GetBoolean());
        Assert.Equal(100, root.GetProperty("RawScore").GetDouble());
        Assert.Equal("Judge outcome: Yes", root.GetProperty("Explanation").GetString());
        Assert.Equal(0, root.GetProperty("TokensUsed").GetInt32());
        Assert.Equal(1, root.GetProperty("LlmCallCount").GetInt32());
        Assert.Equal(1, root.GetProperty("AttemptCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("SafeFailureCode").ValueKind);

        // And the only additions are the three call-accounting counters.
        var added = root.EnumerateObject()
            .Select(p => p.Name)
            .Except(["Status", "Correct", "RawScore", "Explanation", "TokensUsed", "LlmCallCount", "AttemptCount", "SafeFailureCode"])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["AttemptsUsed", "PrimaryLlmCallCount", "RetryLlmCallCount"], added);
    }

    [Fact]
    public async Task RetainRawJudgeResponse_StillBoundedToFourThousandNinetySixCharacters()
    {
        var judge = CreateJudge(Response(new string('x', 5000)));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.None,
            RetainRawJudgeResponse = true
        });

        Assert.Equal(4096, result.RawResponse!.Length);
    }

    [Fact]
    public async Task RetainRawJudgeResponse_SeparatesAWrongJudgeFromAnUnparseableOne()
    {
        // The diagnosis the option exists for: an Invalid verdict whose raw text shows the judge was
        // answering sensibly and the wrapper could not read it.
        var judge = CreateJudge(Response("Correct — the response matches."));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.Outcome,
            RetainRawJudgeResponse = true
        });

        Assert.Equal(JudgeOutcomeStatus.Invalid, result.Status);
        Assert.Equal("invalid_response", result.SafeFailureCode);
        Assert.Equal("Correct — the response matches.", result.RawResponse);
    }

    /// <summary>
    /// Documents why "the explanation starts with Judge" is not a usable failure signature: EVERY
    /// rendered explanation starts with "Judge", whatever the outcome. The discriminator is
    /// <see cref="ExternalJudgmentResult.SafeFailureCode"/>, which is null on a clean verdict.
    /// </summary>
    [Theory]
    [InlineData("yes", JudgeOutcomeStatus.Yes, null)]
    [InlineData("no", JudgeOutcomeStatus.No, null)]
    [InlineData("maybe", JudgeOutcomeStatus.Invalid, "invalid_response")]
    [InlineData("   ", JudgeOutcomeStatus.Empty, "empty_response")]
    public async Task ExplanationPrefix_IsNotAFailureSignature_SafeFailureCodeIs(
        string judgeText,
        JudgeOutcomeStatus expectedStatus,
        string? expectedFailureCode)
    {
        var judge = CreateJudge(Response(judgeText));

        var result = await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeEvidenceMode = JudgeEvidenceMode.Outcome
        });

        Assert.Equal(expectedStatus, result.Status);
        // True on success AND on failure — so it discriminates nothing.
        Assert.StartsWith("Judge", result.Explanation, StringComparison.Ordinal);
        // This is the field that actually separates them.
        Assert.Equal(expectedFailureCode, result.SafeFailureCode);
    }

    [Fact]
    public async Task JudgeTemperature_DefaultsToNull_WhichIsProviderDefaultNotZero()
    {
        var client = new RecordingChatClient(Response("yes"));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions { MaxJudgeRetries = 0 });

        // Deliberate: an explicit temperature is rejected by reasoning-model deployments. Documented
        // here so "the judge is deterministic by default" is never assumed.
        Assert.Null(client.LastOptions!.Temperature);
    }

    [Fact]
    public async Task JudgeTemperature_Zero_IsSentAsZeroAndNotDroppedAsFalsy()
    {
        var client = new RecordingChatClient(Response("yes"));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeTemperature = 0
        });

        // Pins the deterministic configuration: 0 must survive the double? -> float? conversion rather
        // than collapsing to null and silently restoring provider-default sampling.
        Assert.Equal(0f, client.LastOptions!.Temperature);
    }

    [Fact]
    public async Task JudgeTemperature_ZeroUnderStructuredProtocol_IsAlsoSent()
    {
        var client = new RecordingChatClient(Response("""{"verdict": "yes", "reasoning": "ok"}"""));
        var judge = new LongMemEvalJudge(client, NullLogger<LongMemEvalJudge>.Instance);

        await judge.JudgeAsync("answer", Question(), new ExternalBenchmarkOptions
        {
            MaxJudgeRetries = 0,
            JudgeTemperature = 0,
            JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson
        });

        Assert.Equal(0f, client.LastOptions!.Temperature);
    }
}
