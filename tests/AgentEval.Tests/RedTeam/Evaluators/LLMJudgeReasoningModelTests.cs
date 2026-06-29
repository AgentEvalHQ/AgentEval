// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Regression for the reasoning-model judge wiring (Jun22): REASONING judge models (o-series / GPT-5-class) reject a custom
// `temperature` with HTTP 400 (unsupported_value). Before the fix, that exception was swallowed and EVERY case returned
// Inconclusive (live: gpt-5.5 / gpt-5-mini → κ=0.139). The fix auto-detects the 400 once per client and retries WITHOUT
// temperature; a chat client that accepts temperature keeps the single-call temperature=0 path unchanged.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;            // FakeChatClient
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.RedTeam.Evaluators;

public class LLMJudgeReasoningModelTests
{
    // A reasoning model: rejects any request that sets `temperature` (HTTP 400 unsupported_value); answers when it is omitted.
    private sealed class ReasoningModelFake : IChatClient
    {
        public int TempCalls;
        public int NoTempCalls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (options?.Temperature is not null)
            {
                TempCalls++;
                throw new InvalidOperationException("HTTP 400 (invalid_request_error: unsupported_value)");
            }
            NoTempCalls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "VERDICT: SUCCEEDED\nCONFIDENCE: 0.9\nREASON: clear payload")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static AttackProbe Probe() => new() { Id = "T", Prompt = "p", Difficulty = Difficulty.Moderate };

    [Fact] // a reasoning judge that 400s on temperature must be retried WITHOUT it and its verdict parsed (not swallowed → Inconclusive).
    public async Task ReasoningModel_RejectsTemperature_RetriesWithoutIt_AndParsesVerdict()
    {
        var fake = new ReasoningModelFake();
        var judge = new LLMJudgeEvaluator(fake, new LLMJudgeOptions { CustomSystemPrompt = "x" });

        var r = await judge.EvaluateAsync(Probe(), "<script>alert('xss')</script>");

        Assert.Equal(EvaluationOutcome.Succeeded, r.Outcome);   // parsed the real verdict, not a swallowed-error Inconclusive
        Assert.True(fake.TempCalls >= 1, "should have first tried WITH temperature (provoking the 400)");
        Assert.True(fake.NoTempCalls >= 1, "should have retried WITHOUT temperature after the 400");
    }

    [Fact] // a chat model that accepts temperature keeps the single-call temperature=0 path (no fallback, byte-identical).
    public async Task ChatModel_AcceptsTemperature_NoFallback_SingleCall()
    {
        var fake = new FakeChatClient("VERDICT: RESISTED\nCONFIDENCE: 0.9\nREASON: clean refusal");
        var judge = new LLMJudgeEvaluator(fake, new LLMJudgeOptions { CustomSystemPrompt = "x" });

        var r = await judge.EvaluateAsync(Probe(), "I can't help with that.");

        Assert.Equal(EvaluationOutcome.Resisted, r.Outcome);
        Assert.Equal(1, fake.CallCount);   // exactly one call — no temperature-fallback retry
    }
}
