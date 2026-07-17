// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper M4 — the STATEFUL half of the Crescendo-trajectory defense: <see cref="CrescendoTrajectoryJudge"/>
/// tracks a rolling window of turns and arms quarantine once <see cref="CrescendoTrajectoryJudge.DefaultArmThreshold"/>
/// escalating turn-shifts have been observed. These tests drive the full agent pipeline (matches
/// <c>ShadowJudgeTests</c>'s own convention) rather than constructing an <c>AgentSession</c> directly, since the
/// session type has no public test-friendly constructor.
/// </summary>
/// <remarks>
/// <b>Why a semaphore, not <c>CompleteAndDrainAsync</c>, between turns.</b> <c>ShadowJudgePump.CompleteAndDrainAsync</c>
/// permanently completes the pump's channel writer (it is a terminal, dispose-adjacent operation, meant to be
/// called at most once) — calling it mid-sequence would silently drop every later <c>Enqueue</c>, since a
/// completed channel refuses further writes. These tests instead release a semaphore from <c>onVerdict</c> and
/// await it once per turn, which waits for exactly that turn's judgement without ending the pump's life.
/// </remarks>
public class CrescendoTrajectoryJudgeTests
{
    private static readonly TimeSpan VerdictTimeout = TimeSpan.FromSeconds(5);

    private static ChatClientAgent Agent(ScriptedChatClient scripted)
        => new(scripted, new ChatClientAgentOptions { Name = "T" });

    private static ScriptedChatClient JudgeReplying(params bool[] escalatesInOrder)
    {
        var client = new ScriptedChatClient();
        foreach (var escalates in escalatesInOrder)
        {
            client.AddText(escalates
                ? """{"escalates": true, "confidence": 0.9, "evidence": "shift"}"""
                : """{"escalates": false, "confidence": 0.9}""");
        }

        return client;
    }

    private static async Task RunAndAwaitVerdictAsync(AIAgent agent, string text, AgentSession session, SemaphoreSlim verdictSignal)
    {
        await agent.RunAsync(text, session);
        var signalled = await verdictSignal.WaitAsync(VerdictTimeout);
        Assert.True(signalled, "timed out waiting for the shadow judge to process this turn");
    }

    [Fact]
    public void Constants_MatchDesignedThresholds()
    {
        Assert.Equal("gatekeeper.crescendo.state", CrescendoTrajectoryJudge.StateKey);
        Assert.Equal(5, CrescendoTrajectoryJudge.DefaultMaxTrackedTurns);
        Assert.Equal(3, CrescendoTrajectoryJudge.DefaultArmThreshold);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveThresholds()
    {
        var model = new ScriptedChatClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrescendoTrajectoryJudge(model, maxTrackedTurns: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CrescendoTrajectoryJudge(model, armThreshold: 0));
    }

    [Fact]
    public async Task JudgeAsync_NoSession_NeverArms_EvenOnEscalatingReply()
    {
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(true));

        var verdict = await judge.JudgeAsync(new ShadowJudgeContext("dangerous reply", "dangerous ask", "T", Session: null));

        Assert.False(verdict.Compromised);   // a single unpersisted turn is not a pattern — never arms alone
    }

    [Fact]
    public async Task JudgeAsync_EmptyTurn_ReturnsCleanWithoutCallingModel()
    {
        var model = new ScriptedChatClient().AddThrow();   // would blow up if called
        var judge = new CrescendoTrajectoryJudge(model);

        var verdict = await judge.JudgeAsync(new ShadowJudgeContext(string.Empty, null, "T", Session: null));

        Assert.False(verdict.Compromised);
    }

    [Fact]
    public async Task ThreeEscalatingTurns_ArmQuarantine_FourthRunStillSucceeds_FifthRunFailsClosed()
    {
        // Turns 1-4 each return normally (a shadow judge never blocks the run it's judging); only after turn 4's
        // verdict (the 3rd escalation) is processed does quarantine arm, so turn 5 is the first one refused.
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(false, true, true, true));
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3").AddText("r4");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "benign turn", session, verdictSignal);      // count=0
        await RunAndAwaitVerdictAsync(agent, "escalation 1", session, verdictSignal);     // count=1, below threshold
        await RunAndAwaitVerdictAsync(agent, "escalation 2", session, verdictSignal);     // count=2, below threshold
        var r4 = await agent.RunAsync("escalation 3", session);
        Assert.Equal("r4", r4.Text);   // still succeeds — the shadow judge never blocks the run it observed
        Assert.True(await verdictSignal.WaitAsync(VerdictTimeout));   // count=3 -> arms quarantine

        var ex = await Record.ExceptionAsync(() => agent.RunAsync("turn 5", session));
        Assert.IsType<EvalGateRefusalException>(ex);
    }

    [Fact]
    public async Task TwoEscalatingTurns_BelowThreshold_NeverArms()
    {
        // 3 scripted replies for 3 judged turns (2 escalating + 1 benign) — every turn that gets a RunAsync call
        // must have a matching scripted reply, else the judge model call falls through ScriptedChatClient's
        // exhausted-queue fallback (an empty reply -> Inconclusive -> previously fail-closed to "escalating").
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(true, true, false));
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3").AddText("r4");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "escalation 1", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "escalation 2", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "benign turn", session, verdictSignal);

        var fourth = await agent.RunAsync("still fine", session);   // only 2 escalations ever observed -> never armed
        Assert.Equal("r4", fourth.Text);
    }

    [Fact]
    public async Task EscalationCount_IsNotWindowed_SlowBurnAcrossManyBenignTurnsStillArms()
    {
        // 3 escalations spread across 9 total turns (more than MaxTrackedTurns=5) — the summary window rolls
        // over several times, but the escalation counter is a lifetime total, not windowed, so it still arms.
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(false, false, true, false, false, true, false, true));
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient();
        for (var i = 0; i < 8; i++)
        {
            scripted.AddText($"turn-{i}");
        }

        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        for (var i = 0; i < 8; i++)
        {
            await RunAndAwaitVerdictAsync(agent, $"turn {i}", session, verdictSignal);
        }

        // The 8 processed judgements (false,false,true,false,false,true,false,true) yield exactly 3 escalations.
        var ex = await Record.ExceptionAsync(() => agent.RunAsync("turn 9", session));
        Assert.IsType<EvalGateRefusalException>(ex);
    }

    [Fact]
    public async Task CompromisedVerdict_ReasonCitesEscalationCountAndThreshold()
    {
        ShadowVerdict? sunk = null;
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(true, true, true));
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (v, _) =>
        {
            sunk = v;
            verdictSignal.Release();
        });

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3");
        var agent = Agent(scripted).AsBuilder().UseAgentEvalShadowJudge(pump).Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "escalation 1", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "escalation 2", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "escalation 3", session, verdictSignal);

        Assert.NotNull(sunk);
        Assert.True(sunk!.Compromised);
        Assert.Contains("3", sunk.Reason);
    }

    [Fact]
    public async Task ThreeConsecutiveJudgeFailures_NeverCountAsEscalations_DoesNotArm()
    {
        // A judge-model error is Inconclusive, not a detected escalation — FailClosedOnInconclusive is forced
        // false internally, so 3 straight infra failures on an otherwise benign session must not arm quarantine.
        var failingModel = new ScriptedChatClient().AddThrow().AddThrow().AddThrow();
        var judge = new CrescendoTrajectoryJudge(failingModel);
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3").AddText("r4");
        var agent = Agent(scripted).AsBuilder()
            .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
            .UseAgentEvalShadowJudge(pump)
            .Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "turn 1", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "turn 2", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "turn 3", session, verdictSignal);

        var fourth = await agent.RunAsync("still fine", session);   // 3 judge failures, never counted as escalation
        Assert.Equal("r4", fourth.Text);
    }

    [Fact]
    public async Task OnceArmed_SubsequentEscalatingTurns_DoNotReArm_OnlyOneCompromiseVerdictEmitted()
    {
        var judge = new CrescendoTrajectoryJudge(JudgeReplying(true, true, true, true));   // arms on the 3rd
        var compromiseCount = 0;
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (v, _) =>
        {
            if (v.Compromised)
            {
                Interlocked.Increment(ref compromiseCount);
            }

            verdictSignal.Release();
        });

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3").AddText("r4");
        var agent = Agent(scripted).AsBuilder().UseAgentEvalShadowJudge(pump).Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "t1", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "t2", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "t3", session, verdictSignal);   // crosses the threshold — arms here
        await RunAndAwaitVerdictAsync(agent, "t4", session, verdictSignal);   // already armed — must not re-fire

        Assert.Equal(1, compromiseCount);
    }

    [Fact]
    public async Task Summarize_FlagsPriorEscalatingTurn_WithItsTrueLifetimeIndex()
    {
        var judgeModel = JudgeReplying(true, false, false, false);   // turn 1 escalates; turns 2-4 benign
        var judge = new CrescendoTrajectoryJudge(judgeModel);
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient().AddText("r1").AddText("r2").AddText("r3").AddText("r4");
        var agent = Agent(scripted).AsBuilder().UseAgentEvalShadowJudge(pump).Build();
        var session = await agent.CreateSessionAsync();

        await RunAndAwaitVerdictAsync(agent, "turn 1", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "turn 2", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "turn 3", session, verdictSignal);
        await RunAndAwaitVerdictAsync(agent, "turn 4", session, verdictSignal);

        // The 4th call's summary covers turns 1-3; turn 1 (the escalating one) must be flagged and labeled with
        // its true lifetime index — matching the exact "Turn N [flagged]: ..." shape the rubric's gold set uses.
        var lastPrompt = judgeModel.ReceivedMessages[^1].First().Text;
        Assert.Contains("Turn 1 [flagged]:", lastPrompt);
        Assert.Contains("Turn 3:", lastPrompt);
    }

    [Fact]
    public async Task Summarize_AfterWindowRollover_LabelsTurnsByLifetimeIndex_NotWindowPosition()
    {
        var judgeModel = JudgeReplying(false, false, false, false, false, false, false);   // 7 benign judged turns
        var judge = new CrescendoTrajectoryJudge(judgeModel);
        var verdictSignal = new SemaphoreSlim(0);
        await using var pump = new ShadowJudgePump(judge, onVerdict: (_, _) => verdictSignal.Release());

        var scripted = new ScriptedChatClient();
        for (var i = 0; i < 7; i++)
        {
            scripted.AddText($"r{i}");
        }

        var agent = Agent(scripted).AsBuilder().UseAgentEvalShadowJudge(pump).Build();
        var session = await agent.CreateSessionAsync();

        for (var i = 0; i < 7; i++)
        {
            await RunAndAwaitVerdictAsync(agent, $"turn {i}", session, verdictSignal);
        }

        // The window (default size 5) only evicts its oldest entry on the append AFTER it's already full, so the
        // 6th call's summary is still pre-rollover (turns 1-5) — it takes a 7th call to see the rolled-over
        // window (turns 2-6). That 7th call's summary must label the most recent tracked prior turn "Turn 6" —
        // its true lifetime index — never relabeling it "Turn 1" from its position within the window.
        var lastPrompt = judgeModel.ReceivedMessages[^1].First().Text;
        Assert.Contains("Turn 6:", lastPrompt);
        Assert.DoesNotContain("Turn 1:", lastPrompt);
    }
}
