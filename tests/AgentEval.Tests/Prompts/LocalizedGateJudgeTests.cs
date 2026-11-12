// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Prompts;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Prompts;

public sealed class LocalizedGateJudgeTests
{
    private const string Block = """{"decision":"BLOCKED","confidence":0.8,"rationale":"Avböjer","evidence":["Nej"]}""";
    private const string Allow = """{"decision":"ALLOWED","confidence":0.9,"rationale":"Besvarar","evidence":[]}""";
    private static IJudgeRubric[] Rubrics => [new OverRefusalRubric(), new IndirectInjectionRubric(), new ExfiltrationIntentRubric(),
        new SystemPromptExtractionRubric(), new UngroundedClaimRubric(), new IntentActionMismatchRubric(), new GoalHijackDriftRubric(),
        new ToolArgumentGoalCoherenceRubric(), new InterAgentOutboundGoalDriftRubric(), new CrescendoTrajectoryRubric()];
    private static JudgePromptOptions Selection(IJudgeRubric? rubric = null, double threshold = 0, bool capture = false) => new()
    {
        Locale = "sv-SE", CaptureContent = capture,
        Override = GateJudgePrompts.ForRubric(rubric ?? new OverRefusalRubric(), threshold) with
        { Locale = "sv-SE", SystemText = "Bedöm endast det inhägnade underlaget.", RetryText = "Svara med giltig JSON." }
    };
    private static CompositeJudgeGate<OverRefusalRubric> Gate(IChatClient client, JudgeGateOptions? options = null) =>
        new(new(), client, options ?? new() { JudgePrompts = Selection(), ModelId = "requested-model" });

    [Fact]
    public async Task JudgeText_SwedishPrefilterMiss_ReachesSelectedJudge()
    {
        using var client = new FakeChatClient(Block);
        var legacy = new CompositeJudgeGate<OverRefusalRubric>(new(), client);
        Assert.Null(await legacy.JudgeTextAsync("Nej, det gör jag inte."));
        Assert.Equal(0, client.CallCount);
        var result = await Gate(client).JudgeTextAsync("Nej, det gör jag inte.");
        Assert.Equal(JudgeDecision.Blocked, result!.Decision);
        Assert.Equal("sv-SE", result.Instrument!.Prompt.EffectiveLocale);
        Assert.Null(result.Instrument.Prompt.Content);
        Assert.Null(Assert.Single(result.Instrument.Calls).ModelId);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Export_AllOwnedRubrics_BindWithoutHiddenLegacyInstructions()
    {
        foreach (var rubric in Rubrics)
        {
            using var client = new FakeChatClient(Allow);
            var gate = new CompositeJudgeGate<IJudgeRubric>(rubric, client, new() { JudgePrompts = Selection(rubric) });
            Assert.Equal(0, client.CallCount);
            var result = await gate.JudgeTextAsync("Svenskt underlag");
            Assert.Equal(JudgeDecision.Allowed, result!.Decision);
            Assert.Equal("gate." + rubric.Axis, result.Instrument!.Prompt.EvaluatorKey);
            Assert.Equal("Bedöm endast det inhägnade underlaget.", Assert.Single(client.ReceivedMessages).First().Text);
        }
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("{\"decision\":\"ALLOWED\"}")]
    [InlineData("{\"decision\":\"ALLOWED\",\"decision\":\"BLOCKED\",\"confidence\":0.9,\"rationale\":\"x\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"ALLOWED\",\"confidence\":2,\"rationale\":\"x\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"allowed\",\"confidence\":0.9,\"rationale\":\"x\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"ALLOWED\",\"confidence\":0.9,\"rationale\":\"\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"ALLOWED\",\"confidence\":0.9,\"rationale\":\"x\",\"evidence\":[1]}")]
    [InlineData("{\"decision\":\"ALLOWED\",\"confidence\":0.9,\"rationale\":\"x\",\"evidence\":[],\"extra\":true}")]
    public async Task JudgeText_InvalidReplies_RetryOnceThenRemainUnmeasured(string invalid)
    {
        using var client = new FakeChatClient(invalid, invalid);
        var result = await Gate(client).JudgeTextAsync("Underlag");
        Assert.Equal(JudgeDecision.Inconclusive, result!.Decision);
        Assert.False(result.Instrument!.Measured);
        Assert.Equal(2, client.CallCount);
        Assert.Equal("Svara med giltig JSON.", client.ReceivedMessages.Last().Last().Text);
    }

    [Theory]
    [InlineData(false, GateAction.Allow)]
    [InlineData(true, GateAction.Block)]
    public async Task Inspect_RetryBudgetExhausted_UsesBudgetPolicy(bool failClosed, GateAction expected)
    {
        using var client = new FakeChatClient("bad", Block);
        var gate = Gate(client, new() { JudgePrompts = Selection(), SpendGovernor = new(1, 100000), FailClosedOnBudgetExhausted = failClosed });
        var result = await gate.InspectAsync("Underlag");
        Assert.Equal(expected, result.Action);
        Assert.True(result.Provenance!.JudgeInstrument!.BudgetExhausted);
        Assert.False(result.Provenance.JudgeInstrument.Measured);
        Assert.Equal(1, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_UnmeasuredFailOpen_DoesNotPersistAnAllow(bool panel)
    {
        using var client = new FakeChatClient("bad", "bad", Allow);
        IChatGate gate = Gate(client, new() { JudgePrompts = Selection(), FailClosedOnInconclusive = false });
        var cache = new JudgeVerdictCache(panel ? new ParallelJudgeFanOut([gate]) : gate);
        Assert.Equal(GateAction.Allow, (await cache.InspectAsync("Underlag")).Action);
        Assert.Equal(0, cache.Count);
        Assert.Equal(GateAction.Allow, (await cache.InspectAsync("Underlag")).Action);
        Assert.Equal(1, cache.Count);
        await cache.InspectAsync("Underlag");
        Assert.Equal(3, client.CallCount);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public async Task Inspect_SelectedThreshold_PreservesRawVerdictAndChangesEnforcement()
    {
        using var client = new FakeChatClient(Block, Block);
        var gate = Gate(client, new() { JudgePrompts = Selection(threshold: .9), BlockThreshold = 0 });
        var raw = await gate.JudgeTextAsync("Nej");
        Assert.Equal(JudgeDecision.Blocked, raw!.Decision);
        Assert.False(gate.IsFlagged(raw));
        var enforced = await gate.InspectAsync("Nej");
        Assert.Equal(GateAction.Allow, enforced.Action);
        Assert.Equal(.9, enforced.Provenance!.Threshold);
        Assert.True(enforced.Provenance.JudgeInstrument!.Measured);
    }

    [Fact]
    public async Task Inspect_LongInput_BoundsOnlyEvidenceAndCapturesActualRenderedPrompt()
    {
        using var client = new FakeChatClient(Allow);
        var gate = Gate(client, new() { JudgePrompts = Selection(capture: true), MaxInputChars = 256 });
        var result = await gate.InspectAsync("HEAD" + new string('x', 2000) + "TAIL");
        var text = client.ReceivedMessages.Single().Last().Text!;
        Assert.Contains("HEAD", text); Assert.Contains("TAIL", text); Assert.DoesNotContain(new string('x', 300), text);
        Assert.NotNull(result.Provenance!.JudgeInstrument!.Prompt.Content);
        Assert.Equal(gate.InstrumentDigest, result.Provenance.JudgeInstrument.ConfigurationDigest);
    }

    [Fact]
    public void Construct_MissingLocaleOrUnknownRubric_FailsBeforeCalls()
    {
        using var client = new FakeChatClient(Allow);
        Assert.Throws<ArgumentException>(() => Gate(client, new() { JudgePrompts = new() { Locale = "sv-SE" } }));
        Assert.Throws<NotSupportedException>(() => new CompositeJudgeGate<UnknownRubric>(new(), client, new() { JudgePrompts = Selection() }));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void Identity_ChangedPromptLocaleModelAndPolicy_ChangesDigestWithoutCalls()
    {
        using var client = new FakeChatClient(Allow);
        var selection = Selection();
        var variants = new JudgeGateOptions[]
        {
            new() { JudgePrompts = selection, ModelId = "a" },
            new() { JudgePrompts = selection, ModelId = "b" },
            new() { JudgePrompts = selection, ModelId = "a", MaxInputChars = 256 },
            new() { JudgePrompts = selection, ModelId = "a", FailClosedOnInconclusive = false },
            new() { JudgePrompts = Selection(threshold: .5), ModelId = "a" },
            new() { JudgePrompts = new() { Locale = "en" }, ModelId = "a" },
            new() { JudgePrompts = selection, ModelId = "a", SpendGovernor = new(1, 1000) }
        };
        Assert.Equal(variants.Length, variants.Select(o => Gate(client, o).InstrumentDigest).Distinct().Count());
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Inspect_EmptyConfiguredEvidence_IsUnmeasuredWithoutACall(string text)
    {
        using var client = new FakeChatClient(Allow);
        var result = await Gate(client).InspectAsync(text);
        Assert.Equal(GateAction.Block, result.Action);
        Assert.False(result.Provenance!.JudgeInstrument!.Measured);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Inspect_TimeoutAndCallerCancellation_RemainDistinct()
    {
        using var slow = new CallbackClient(async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        var gate = Gate(slow, new() { JudgePrompts = Selection(), Timeout = TimeSpan.FromMilliseconds(50) });
        var result = await gate.InspectAsync("Underlag");
        Assert.Equal(GateAction.Block, result.Action);
        Assert.False(result.Provenance!.JudgeInstrument!.Measured);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.JudgeTextAsync("", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new JudgeVerdictCache(gate).InspectAsync("", cancelled.Token));
    }

    [Fact]
    public async Task Calibration_ConfiguredFailure_CannotBecomeReadinessThroughFailClosedAction()
    {
        using var client = new FakeChatClient("bad", "bad", Allow);
        var report = await GateCalibrationHarness.EvaluateAsync(Gate(client), Gold(), Options());
        Assert.Equal(1, report.UnmeasuredCases);
        Assert.Equal(1, report.Total);
        Assert.False(report.Cases[0].Correct);
        Assert.False(report.IsInlineReady);
    }

    [Fact]
    public async Task Readiness_ChangedInstrument_RejectsPreviouslyPassingCalibrationAfterStoreRoundTrip()
    {
        var replies = new Queue<string>([Block, Allow]);
        using var client = new CallbackClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies.Dequeue())) { ModelId = "observed-model" }));
        var gate = Gate(client);
        var report = await GateCalibrationHarness.EvaluateAsync(new JudgeVerdictCache(gate), Gold(), Options());
        Assert.True(report.IsInlineReady);
        Assert.Equal(gate.InstrumentDigest, Assert.Single(report.InstrumentDigests));
        Assert.Equal(gate.InstrumentDigest, report.CalibratedInstrumentDigest);
        var directory = Path.Combine(Path.GetTempPath(), "agenteval-localized-calibration-" + Guid.NewGuid().ToString("N"));
        try
        {
#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001
            var store = new JsonFileCalibrationReportStore(directory);
            await store.SaveAsync(report);
            var loaded = await store.LoadLatestAsync("over-refusal");
            Assert.Equal("synthetic-test", loaded!.SplitLabel);
            Assert.Equal(report.InstrumentDigests, loaded.InstrumentDigests);
            Assert.Equal(report.CalibratedInstrumentDigest, loaded.CalibratedInstrumentDigest);
            var options = new GatekeeperOptions { CalibrationReportStore = store };
            options.PreGates.Add(new JudgeVerdictCache(gate));
            await options.ValidateInlineJudgesAsync();
            var path = Assert.Single(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
            var oldReport = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            Assert.True(oldReport.Remove("CalibratedInstrumentDigest"));
            await File.WriteAllTextAsync(path, oldReport.ToJsonString());
            Assert.Null((await store.LoadLatestAsync("over-refusal"))!.CalibratedInstrumentDigest);
            await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
            await store.SaveAsync(report);
            var changed = Gate(client, new() { JudgePrompts = Selection(threshold: .9), ModelId = "requested-model" });
            options.PreGates.Clear();
            options.PreGates.Add(new JudgeVerdictCache(changed));
            await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
            var legacy = await GateCalibrationHarness.EvaluateAsync(new LegacyPerfectGate(), Gold(), Options());
            Assert.True(legacy.IsInlineReady);
            await store.SaveAsync(legacy);
            options.PreGates.Clear(); options.PreGates.Add(gate);
            await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
#pragma warning restore AGENTEVAL_GATEKEEPER_PREVIEW001
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_PassingPanel_DoesNotCertifyAnIndividualContributor(bool legacyCompanion)
    {
        var replies = new Queue<string>([Block, Allow]);
        using var strongClient = new CallbackClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies.Dequeue())) { ModelId = "observed-strong" }));
        using var weakClient = new CallbackClient(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Allow)) { ModelId = "observed-weak" }));
        var weak = Gate(weakClient);
        IChatGate strong = legacyCompanion ? new LegacyPerfectGate()
            : Gate(strongClient, new() { JudgePrompts = Selection(), ModelId = "strong-model" });
        var report = await GateCalibrationHarness.EvaluateAsync(new ParallelJudgeFanOut([strong, weak]), Gold(), Options());
        Assert.True(report.IsInlineReady);
        Assert.Equal(legacyCompanion ? 1 : 2, report.InstrumentDigests.Count);
        Assert.Null(report.CalibratedInstrumentDigest);
        var directory = Path.Combine(Path.GetTempPath(), "agenteval-panel-calibration-" + Guid.NewGuid().ToString("N"));
        try
        {
#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001
            var store = new JsonFileCalibrationReportStore(directory);
            await store.SaveAsync(report);
            var options = new GatekeeperOptions { CalibrationReportStore = store };
            options.PreGates.Add(new JudgeVerdictCache(weak));
            await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
#pragma warning restore AGENTEVAL_GATEKEEPER_PREVIEW001
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Calibration_UnreportedModel_DoesNotClaimReadiness()
    {
        using var client = new FakeChatClient(Block, Allow);
        var report = await GateCalibrationHarness.EvaluateAsync(Gate(client), Gold(), Options());
        Assert.Equal(1, report.DecisiveAccuracy);
        Assert.False(report.ModelsIdentified);
        Assert.False(report.IsInlineReady);
    }

    private static JudgeGoldSet Gold() => new("over-refusal", [new("Nej", true), new("Svar", false)]);
    private static CalibrationOptions Options() => new() { MaxConcurrency = 1, MinCasesPerDirection = 1, MinDecisiveAccuracy = 1, SplitLabel = "synthetic-test" };
    private sealed class LegacyPerfectGate : IChatGate
    {
        public string PolicyName => "legacy-test";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(text == "Nej" ? GateVerdict.Block(PolicyName, "test") : GateVerdict.Allow(PolicyName));
    }
    private sealed class UnknownRubric : IJudgeRubric
    {
        public string Axis => "unknown";
        public bool Prefilter(string text) => throw new InvalidOperationException();
        public string BuildPrompt(string text) => throw new InvalidOperationException();
        public JudgeVerdict Parse(string text) => throw new InvalidOperationException();
    }
    private sealed class CallbackClient(Func<CancellationToken, Task<ChatResponse>> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => reply(cancellationToken);
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
