// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Gatekeeper M4 — the shadow judge: async off-hot-path judgement that arms quarantine for a later run.</summary>
public class ShadowJudgeTests
{
    private static ChatClientAgent Agent(ScriptedChatClient scripted)
        => new(scripted, new ChatClientAgentOptions { Name = "T" });

    [Fact]
    public async Task ShadowJudge_CompromisedVerdict_ArmsQuarantine_NextRunFailsClosed()
    {
        // The closed loop: run 1 is judged (async) compromised => quarantine armed; run 2 (same session) is
        // refused by the QuarantineGate — even though the judge was too expensive to run inline.
        await using var pump = new ShadowJudgePump(new KeywordShadowJudge("leak"));
        var scripted = new ScriptedChatClient().AddText("sure, here is the leak").AddText("second answer");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)   // reads the flag
            .UseAgentEvalShadowJudge(pump)                                                        // arms the flag
            .Build();
        var session = await agent.CreateSessionAsync();

        // run 1: returns normally (the shadow judge never blocks it)
        var first = await agent.RunAsync("go", session);
        Assert.Equal("sure, here is the leak", first.Text);

        await pump.CompleteAndDrainAsync();   // let the async judgement finish + arm quarantine

        // run 2: the same session is now quarantined => fails closed
        var ex = await Record.ExceptionAsync(() => agent.RunAsync("continue", session));
        Assert.IsType<EvalGateRefusalException>(ex);
    }

    [Fact]
    public async Task ShadowJudge_CleanVerdict_DoesNotQuarantine()
    {
        await using var pump = new ShadowJudgePump(new KeywordShadowJudge("leak"));
        var scripted = new ScriptedChatClient().AddText("a helpful, clean answer").AddText("second answer");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("go", session);
        await pump.CompleteAndDrainAsync();

        var second = await agent.RunAsync("continue", session);   // not quarantined => proceeds
        Assert.Equal("second answer", second.Text);
    }

    [Fact]
    public async Task ShadowJudge_ReceivesResponseSnapshot_AndVerdictReachesSink()
    {
        ShadowJudgeContext? seen = null;
        ShadowVerdict? sunk = null;
        await using var pump = new ShadowJudgePump(
            new KeywordShadowJudge("leak", capture: c => seen = c),
            onVerdict: (v, _) => sunk = v);
        var agent = Agent(new ScriptedChatClient().AddText("the leak is here")).AsBuilder()
            .UseAgentEvalShadowJudge(pump).Build();

        await agent.RunAsync("go");
        await pump.CompleteAndDrainAsync();

        Assert.Equal("the leak is here", seen!.ResponseText);   // the judge saw the response snapshot
        Assert.True(sunk!.Compromised);                         // and the verdict reached the sink (the store)
    }

    [Fact]
    public async Task ShadowJudge_ThrowingJudge_IsReportedNotFatal()
    {
        Exception? reported = null;
        await using var pump = new ShadowJudgePump(new ThrowingShadowJudge(), onError: ex => reported = ex);
        var agent = Agent(new ScriptedChatClient().AddText("answer")).AsBuilder()
            .UseAgentEvalShadowJudge(pump).Build();

        var response = await agent.RunAsync("go");   // the run is unaffected by a broken judge
        await pump.CompleteAndDrainAsync();

        Assert.Equal("answer", response.Text);
        Assert.NotNull(reported);   // the failure was reported, not thrown into the run
    }

    // ── streaming seam ──

    [Fact]
    public async Task ShadowJudge_Streaming_FullyConsumed_JudgesAccumulatedText_ArmsQuarantine()
    {
        await using var pump = new ShadowJudgePump(new KeywordShadowJudge("leak"));
        var scripted = new ScriptedChatClient().AddText("the leak is here").AddText("second answer");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        // fully consume the stream (the seam accumulates text, then enqueues after the loop)
        await foreach (var _ in agent.RunStreamingAsync("go", session)) { }
        await pump.CompleteAndDrainAsync();

        var ex = await Record.ExceptionAsync(() => agent.RunAsync("continue", session));
        Assert.IsType<EvalGateRefusalException>(ex);   // streamed response was judged + quarantined
    }

    [Fact]
    public async Task ShadowJudge_Streaming_AbandonedEarly_EnqueuesNothing()
    {
        var judged = 0;
        await using var pump = new ShadowJudgePump(new KeywordShadowJudge("leak", capture: _ => Interlocked.Increment(ref judged)));
        var scripted = new ScriptedChatClient().AddText("the leak is here").AddText("more");
        var agent = Agent(scripted).AsBuilder().UseAgentEvalShadowJudge(pump).Build();

        // break out of the stream before it completes — the seam must NOT enqueue a partial/absent response
        await foreach (var _ in agent.RunStreamingAsync("go"))
        {
            break;
        }

        await pump.CompleteAndDrainAsync();
        Assert.Equal(0, judged);   // an abandoned stream judges nothing
    }

    [Fact]
    public async Task ShadowJudge_ThrowingOnError_DoesNotKillPump()
    {
        // A judge throws on item 1 AND the onError sink throws too — the pump must survive and still judge (and
        // quarantine on) item 2.
        await using var pump = new ShadowJudgePump(
            new ThrowThenKeywordJudge("leak"),
            onError: _ => throw new InvalidOperationException("broken sink"));
        var scripted = new ScriptedChatClient().AddText("first").AddText("the leak").AddText("third");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("go1", session);   // item 1: judge throws -> onError throws (must not kill pump)
        await agent.RunAsync("go2", session);   // item 2: "the leak" -> compromise -> arm
        await pump.CompleteAndDrainAsync();

        var ex = await Record.ExceptionAsync(() => agent.RunAsync("go3", session));
        Assert.IsType<EvalGateRefusalException>(ex);   // the pump survived item 1 and armed on item 2
    }

    [Fact]
    public async Task ShadowJudge_HangingJudge_DisposeDoesNotHang()
    {
        // A judge that never returns must not hang Dispose — the bounded drain cancels and moves on.
        var pump = new ShadowJudgePump(new HangingShadowJudge(), drainTimeout: TimeSpan.FromMilliseconds(200));
        var agent = Agent(new ScriptedChatClient().AddText("answer")).AsBuilder().UseAgentEvalShadowJudge(pump).Build();
        await agent.RunAsync("go");

        var dispose = pump.DisposeAsync().AsTask();
        // Dispose is bounded (the 200 ms drain cancels the hanging judge), so it always completes quickly; the
        // generous 30 s ceiling only guards against a real hang, and tolerates thread starvation on a loaded runner.
        var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(dispose, finished);   // Dispose completed within the timeout, did not hang on the judge
    }

    [Fact]
    public async Task ShadowJudge_AbandonedConsumer_ResumesWithoutObjectDisposed()
    {
        // A judge that ignores cancellation blocks item 1; Dispose times out, cancels (ignored), abandons the
        // consumer, and disposes _cts. When item 1 unblocks, item 2 must still judge — the captured token must
        // not throw ObjectDisposedException off the disposed source.
        using var gate = new ManualResetEventSlim(false);
        var errors = new List<Exception>();
        var secondJudged = new TaskCompletionSource();
        var pump = new ShadowJudgePump(
            new GatedJudge(gate, onSecond: () => secondJudged.TrySetResult()),
            onError: ex => { lock (errors) { errors.Add(ex); } },
            drainTimeout: TimeSpan.FromMilliseconds(100));
        var agent = Agent(new ScriptedChatClient().AddText("first").AddText("second")).AsBuilder()
            .UseAgentEvalShadowJudge(pump).Build();

        await agent.RunAsync("go1");   // item 1 -> judge blocks on the gate, ignoring cancellation
        await agent.RunAsync("go2");   // item 2
        await pump.DisposeAsync();     // times out, cancels (ignored), abandons the consumer, disposes _cts
        gate.Set();                    // unblock item 1 -> consumer advances to item 2

        var done = await Task.WhenAny(secondJudged.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(secondJudged.Task, done);   // item 2 was judged after the abandoned dispose
        lock (errors)
        {
            Assert.DoesNotContain(errors, e => e is ObjectDisposedException);   // captured token did not throw
        }
    }

    // ── test doubles ──

    private sealed class KeywordShadowJudge : IShadowJudge
    {
        private readonly string _keyword;
        private readonly Action<ShadowJudgeContext>? _capture;
        public KeywordShadowJudge(string keyword, Action<ShadowJudgeContext>? capture = null)
        {
            _keyword = keyword;
            _capture = capture;
        }

        public Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
        {
            _capture?.Invoke(context);
            return Task.FromResult(context.ResponseText.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                ? ShadowVerdict.Compromise($"response contained '{_keyword}'")
                : ShadowVerdict.Clean());
        }
    }

    private sealed class ThrowingShadowJudge : IShadowJudge
    {
        public Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("judge boom");
    }

    private sealed class ThrowThenKeywordJudge : IShadowJudge
    {
        private readonly string _keyword;
        private int _calls;
        public ThrowThenKeywordJudge(string keyword) => _keyword = keyword;
        public Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("first judgement fails");
            }

            return Task.FromResult(context.ResponseText.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
                ? ShadowVerdict.Compromise($"response contained '{_keyword}'")
                : ShadowVerdict.Clean());
        }
    }

    private sealed class HangingShadowJudge : IShadowJudge
    {
        public async Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);   // never returns until cancelled
            return ShadowVerdict.Clean();
        }
    }

    private sealed class GatedJudge : IShadowJudge
    {
        private readonly ManualResetEventSlim _gate;
        private readonly Action _onSecond;
        private int _calls;
        public GatedJudge(ManualResetEventSlim gate, Action onSecond)
        {
            _gate = gate;
            _onSecond = onSecond;
        }

        public Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _gate.Wait();   // block item 1, deliberately IGNORING cancellation
                return Task.FromResult(ShadowVerdict.Clean());
            }

            _onSecond();   // item 2 was reached and judged (no ObjectDisposedException)
            return Task.FromResult(ShadowVerdict.Clean());
        }
    }
}
