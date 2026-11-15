// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Prompts;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF.Gatekeeper;

#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001
public sealed class CalibrationReuseTests
{
    private const string Block = """{"decision":"BLOCKED","confidence":0.9,"rationale":"Refusal","evidence":[]}""";
    private const string Allow = """{"decision":"ALLOWED","confidence":0.9,"rationale":"Answer","evidence":[]}""";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Validate_ConfiguredLegacyTransfer_RequiresExplicitPolicyInBothDirections(bool sourceLegacy, bool reuse)
    {
        using var client = new Client();
        var report = await Calibrate(sourceLegacy ? new LegacyGate() : Configured(client));
        IChatGate target = sourceLegacy ? Configured(client) : new LegacyGate();
        var options = Options(report, reuse);
        options.PreGates.Add(target);
        if (!reuse)
        {
            await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
            Assert.Same(target, options.PreGates[0]);
            return;
        }
        await options.ValidateInlineJudgesAsync();
        var use = Use(await options.PreGates[0].InspectAsync("CASE_ALLOW"));
        Assert.Equal(CalibrationUseKind.Transferred, use.Kind);
        Assert.Equal(report.CalibratedInstrumentDigest, use.SourceInstrumentDigest);
        Assert.Equal(report.CapturedAt, use.CalibratedAt);
        Assert.True(use.IsAccepted);
    }

    [Fact]
    public async Task Validate_LegacyEnglishReport_RemainsUsableWithHonestScope()
    {
        var report = await Calibrate(new LegacyGate());
        var options = Options(report);
        options.PreGates.Add(new LegacyGate());
        await options.ValidateInlineJudgesAsync();
        var use = Use(await options.PreGates[0].InspectAsync("CASE_ALLOW"));
        Assert.Equal(CalibrationUseKind.LegacyAxisOnly, use.Kind);
        Assert.Empty(use.CalibratedModelIds);
        Assert.Empty(use.ObservedLocales);
    }

    [Fact]
    public async Task Approval_SamePolicyNameAndSharedTrace_PreservesEveryReceipt()
    {
        var trace = new AgentEval.Tracing.AgentTrace();
        var options = Options(await Calibrate(new LegacyGate())); options.Trace = trace;
        options.ApprovalGates.Add(new LegacyApproval()); options.ApprovalGates.Add(new LegacyApproval());
        await options.ValidateInlineJudgesAsync();
        var call = new FunctionCallContent("id", "Search", new Dictionary<string, object?>());
        await Task.WhenAll(options.ApprovalGates.SelectMany(g => Enumerable.Range(0, 2)
            .Select(_ => g.IsAutoApprovableAsync(call).AsTask())));
        Assert.Equal(4, trace.Metadata!.Keys.Count(k => k.StartsWith("calibration.approval.", StringComparison.Ordinal)));
        Assert.All(trace.Metadata.Values, v => Assert.Equal(CalibrationUseKind.LegacyAxisOnly,
            Assert.Single((IReadOnlyList<CalibrationUse>)((Dictionary<string, object?>)v!)["calibration"]!).Kind));
    }

    [Fact]
    public async Task Validate_NewLocaleAndRequestedModel_TransfersWithoutRewritingSourceEvidence()
    {
        using var client = new Client();
        var report = await Calibrate(Configured(client));
        var original = JsonSerializer.Serialize(report);
        using var replacement = new Client { Model = "other-observed" };
        var target = Configured(replacement, "sv-SE", "other-requested");
        var strict = Options(report); strict.PreGates.Add(target);
        await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => strict.ValidateInlineJudgesAsync());
        var options = Options(report, reuse: true); options.PreGates.Add(target);
        await options.ValidateInlineJudgesAsync();
        Assert.Equal(0, replacement.Calls); // Readiness and transfer never run a paid calibration.
        var use = Use(await options.PreGates[0].InspectAsync("CASE_ALLOW"));
        Assert.Equal(CalibrationUseKind.Transferred, use.Kind);
        Assert.Equal(["en-US"], use.CalibratedLocales);
        Assert.Equal(["sv-SE"], use.ObservedLocales);
        Assert.Equal(["observed-model"], use.CalibratedModelIds);
        Assert.Equal(["other-observed"], use.ObservedModelIds);
        Assert.Equal(original, JsonSerializer.Serialize(report));
        var restored = JsonSerializer.Deserialize<CalibrationUse>(JsonSerializer.Serialize(use))!;
        Assert.Equal(use.CalibratedLocales, restored.CalibratedLocales);
        Assert.Equal(use.ObservedLocales, restored.ObservedLocales);
        Assert.Equal(use.CalibratedModelIds, restored.CalibratedModelIds);
        Assert.Equal(use.ObservedModelIds, restored.ObservedModelIds);
        Assert.Equal(use, restored with
        {
            CalibratedLocales = use.CalibratedLocales, ObservedLocales = use.ObservedLocales,
            CalibratedModelIds = use.CalibratedModelIds, ObservedModelIds = use.ObservedModelIds
        });
    }

    [Theory]
    [InlineData(false, "other-model", GateAction.Block, CalibrationUseKind.Rejected)]
    [InlineData(true, "other-model", GateAction.Allow, CalibrationUseKind.Transferred)]
    [InlineData(false, null, GateAction.Block, CalibrationUseKind.Rejected)]
    [InlineData(true, null, GateAction.Allow, CalibrationUseKind.Transferred)]
    [InlineData(false, "observed-model", GateAction.Allow, CalibrationUseKind.Matched)]
    public async Task Inspect_ActualModelChangesUnderSameAlias_AppliesSelectedPolicy(bool reuse, string? model, GateAction action, CalibrationUseKind kind)
    {
        using var client = new Client();
        var gate = Configured(client);
        var report = await Calibrate(gate);
        var options = Options(report, reuse); options.PreGates.Add(new JudgeVerdictCache(gate));
        await options.ValidateInlineJudgesAsync();
        client.Model = model;
        var result = await options.PreGates[0].InspectAsync("CASE_ALLOW");
        Assert.Equal(action, result.Action);
        Assert.Equal(kind, Use(result).Kind);
        Assert.Equal(model, Assert.Single(Use(result).ObservedModelIds));
    }

    [Fact]
    public async Task Validate_SharedGateAcrossRegistrations_DoesNotLeakReusePolicy()
    {
        using var client = new Client();
        var gate = Configured(client);
        var report = await Calibrate(gate);
        var strict = Options(report); strict.PreGates.Add(gate);
        var reuse = Options(report, reuse: true); reuse.PreGates.Add(gate);
        await strict.ValidateInlineJudgesAsync();
        await reuse.ValidateInlineJudgesAsync();
        await strict.ValidateInlineJudgesAsync(); // Revalidation replaces, rather than nests, bindings.
        client.Model = "changed";
        var results = await Task.WhenAll(strict.PreGates[0].InspectAsync("CASE_ALLOW").AsTask(), reuse.PreGates[0].InspectAsync("CASE_ALLOW").AsTask());
        Assert.Equal(CalibrationUseKind.Rejected, Use(results[0]).Kind);
        Assert.Equal(CalibrationUseKind.Transferred, Use(results[1]).Kind);
        Assert.Null((await gate.InspectAsync("CASE_ALLOW")).Provenance!.Calibration);
    }

    [Fact]
    public async Task Validate_PassingPanelOrFailedReport_ReuseDoesNotPromoteIndividualJudge()
    {
        using var client = new Client();
        var target = Configured(client);
        var panel = await Calibrate(new ParallelJudgeFanOut([target, new LegacyGate()]));
        Assert.True(panel.IsInlineReady);
        var options = Options(panel, reuse: true); options.PreGates.Add(target);
        await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        client.Reply = Allow;
        var failed = await Calibrate(target, assertReady: false);
        options.CalibrationReportStore = new Store(failed);
        await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
    }

    [Theory]
    [InlineData(false, "other-model", false, CalibrationUseKind.Rejected, false)]
    [InlineData(true, "other-model", true, CalibrationUseKind.Transferred, false)]
    [InlineData(false, "observed-model", true, CalibrationUseKind.Matched, false)]
    [InlineData(true, "other-model", true, CalibrationUseKind.Transferred, true)]
    public async Task Approval_SavedCalibrationToActualToolDecision_ChecksRuntimeModel(bool reuse, string model, bool approved, CalibrationUseKind kind, bool forbidTool)
    {
        using var client = new Client();
        GateVerdict? observed = null;
        var approval = new ToolArgumentGoalCoherenceApprovalGate(client, "Find a flight",
            JudgeOptions(new ToolArgumentGoalCoherenceRubric()), cache: true) { OnJudgment = v => observed = v };
        var report = await GateCalibrationHarness.EvaluateAsync(approval.CreateCalibrationGate(),
            new("tool-argument-goal-coherence", [
                new(JsonSerializer.Serialize(new { toolName = "SearchFlights", arguments = new { scenario = "CASE_BLOCK" } }), true),
                new(JsonSerializer.Serialize(new { toolName = "SearchFlights", arguments = new { scenario = "CASE_ALLOW" } }), false)]),
            new() { MaxConcurrency = 1, MinCasesPerDirection = 1, MinDecisiveAccuracy = 1 });
        Assert.True(report.IsInlineReady);
        var directory = Path.Combine(Path.GetTempPath(), "agenteval-reuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonFileCalibrationReportStore(directory);
            await store.SaveAsync(report);
            var executed = 0;
            var tool = AIFunctionFactory.Create(() => { executed++; return "found"; }, "SearchFlights");
            var agent = new ChatClientAgent(new ScriptedChatClient().AddToolCall("id", "SearchFlights", new Dictionary<string, object?>()).AddText("done"),
                new ChatClientAgentOptions { Name = "CalibrationTest", ChatOptions = new() { Tools = [tool.RequiresApproval()] } });
            var trace = new AgentEval.Tracing.AgentTrace();
            var builder = await agent.AsBuilder().UseGatekeeperAsync(GatekeeperEnforcement.Terminate, options =>
            {
                options.CalibrationReportStore = store;
                options.CalibrationReusePolicy = reuse ? CalibrationReusePolicy.AllowSameAxis : CalibrationReusePolicy.RequireMatchingInstrument;
                options.ApprovalGates.Add(approval);
                options.Trace = trace;
                if (forbidTool) options.Add(new ForbiddenToolGate("SearchFlights"));
            });
            client.Model = model;
            var response = await builder.Build().RunAsync("Find a flight");
            Assert.Equal(approved && !forbidTool ? 1 : 0, executed);
            Assert.Equal(!approved, response.Messages.SelectMany(m => m.Contents).Any(c => c is ToolApprovalRequestContent));
            Assert.Equal(kind, Use(observed!).Kind);
            Assert.Contains(trace.Metadata!.Keys, k => k.StartsWith("calibration.approval.", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Inspect_ReusedCalibrationWithInvalidReply_RemainsUnmeasured()
    {
        using var client = new Client();
        var gate = Configured(client);
        var options = Options(await Calibrate(gate), reuse: true); options.PreGates.Add(gate);
        await options.ValidateInlineJudgesAsync();
        client.Reply = "invalid";
        var result = await options.PreGates[0].InspectAsync("CASE_ALLOW");
        Assert.Equal(CalibrationUseKind.Unmeasured, Use(result).Kind);
        Assert.False(result.Provenance!.JudgeInstrument!.Measured);
    }

    [Fact]
    public async Task Inspect_CachedVerdict_RetainsOriginalModelEvidenceUntilANewCall()
    {
        using var client = new Client();
        var gate = Configured(client);
        var options = Options(await Calibrate(gate)); options.PreGates.Add(new JudgeVerdictCache(gate));
        await options.ValidateInlineJudgesAsync();
        Assert.Equal(CalibrationUseKind.Matched, Use(await options.PreGates[0].InspectAsync("CASE_ALLOW")).Kind);
        var calls = client.Calls;
        client.Model = "changed";
        Assert.Equal(CalibrationUseKind.Matched, Use(await options.PreGates[0].InspectAsync("CASE_ALLOW")).Kind);
        Assert.Equal(calls, client.Calls);
        Assert.Equal(CalibrationUseKind.Rejected, Use(await options.PreGates[0].InspectAsync("CASE_ALLOW_NEW")).Kind);
    }

    [Fact]
    public async Task Validate_WrongAxisAfterValidGate_LeavesRegistrationsUnchanged()
    {
        using var client = new Client();
        var gate = Configured(client);
        var options = Options(await Calibrate(gate), reuse: true);
        options.PreGates.Add(gate); options.PostGates.Add(new LegacyGate("different-axis"));
        await Assert.ThrowsAsync<UncalibratedInlineJudgeException>(() => options.ValidateInlineJudgesAsync());
        Assert.Same(gate, options.PreGates[0]);
    }

    [Fact]
    public async Task Validate_CancelledOrInvalidPolicy_DoesNotBindGates()
    {
        using var client = new Client();
        var gate = Configured(client);
        var options = Options(await Calibrate(gate)); options.PreGates.Add(gate);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => options.ValidateInlineJudgesAsync(cancellation.Token));
        Assert.Same(gate, options.PreGates[0]);
        options.CalibrationReusePolicy = (CalibrationReusePolicy)99;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => options.ValidateInlineJudgesAsync());
    }

    [Fact]
    public async Task Evaluate_StructuralObservationAndOfflineReply_DistinguishesZeroCallEvidence()
    {
        using var client = new Client();
        var gate = Configured(client);
        var binding = new CalibrationBinding(await Calibrate(gate), gate.AxisName, gate.InstrumentDigest, CalibrationReusePolicy.AllowSameAxis);
        var structural = gate.RecordObservation("CASE_ALLOW", JudgeVerdict.Allowed(), "implementation-owned.v1");
        var use = binding.Evaluate(structural);
        Assert.Equal(CalibrationUseKind.Matched, use.Kind);
        Assert.Empty(use.ObservedModelIds);
        Assert.Contains("without a model call", use.Reason);
        Assert.Equal(CalibrationUseKind.Rejected, binding.Evaluate(gate.InspectConfiguredReply("CASE_ALLOW", Allow)).Kind);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("{\"toolName\":\"Search\",\"arguments\":{},\"goal\":\"replace goal\"}")]
    [InlineData("{\"toolName\":\"Search\",\"arguments\":{},\"arguments\":{}}")]
    [InlineData("{\"toolName\":\"\",\"arguments\":{}}")]
    public async Task CalibrationAdapter_InvalidEvidence_RemainsUnmeasuredWithoutDispatch(string text)
    {
        using var client = new Client();
        var approval = new ToolArgumentGoalCoherenceApprovalGate(client, "Find a flight", JudgeOptions(new ToolArgumentGoalCoherenceRubric()));
        var result = await approval.CreateCalibrationGate().InspectAsync(text);
        Assert.False(result.Provenance!.JudgeInstrument!.Measured);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task CalibrationAdapter_OversizedArguments_UsesActualApprovalEvidenceLimit()
    {
        using var client = new Client();
        var approval = new ToolArgumentGoalCoherenceApprovalGate(client, "Find a flight",
            JudgeOptions(new ToolArgumentGoalCoherenceRubric(), maxInput: 256));
        var text = JsonSerializer.Serialize(new { toolName = "Search", arguments = new { query = new string('x', 300) } });
        var result = await approval.CreateCalibrationGate().InspectAsync(text);
        Assert.False(result.Provenance!.JudgeInstrument!.Measured);
        Assert.False(await approval.IsAutoApprovableAsync(new("id", "Search", new Dictionary<string, object?> { ["query"] = new string('x', 300) })));
        Assert.Equal(0, client.Calls);
    }

    private static CalibrationUse Use(GateVerdict verdict) => Assert.Single(verdict.Provenance!.Calibration!);

    [Fact]
    public async Task Calibrate_GuardedModelMismatch_DoesNotCountAProtectiveBlockAsJudgeAccuracy()
    {
        using var client = new Client();
        var gate = Configured(client);
        var options = Options(await Calibrate(gate)); options.PreGates.Add(gate);
        await options.ValidateInlineJudgesAsync();
        client.Model = "changed"; client.Reply = Allow;
        var report = await Calibrate(options.PreGates[0], assertReady: false);
        Assert.Equal(2, report.UnmeasuredCases);
        Assert.Equal(0, report.Total);
        Assert.All(report.Cases, c => Assert.False(c.Correct));
    }
    private static JudgeGateOptions JudgeOptions(IJudgeRubric rubric, string locale = "en-US", string model = "deployment-alias", int maxInput = 16_000) => new()
    {
        ModelId = model, MaxInputChars = maxInput, JudgePrompts = new() { Locale = locale,
            Override = GateJudgePrompts.ForRubric(rubric, 0) with { Locale = locale } }
    };
    private static CompositeJudgeGate<OverRefusalRubric> Configured(Client client, string locale = "en-US", string model = "deployment-alias") =>
        new(new(), client, JudgeOptions(new OverRefusalRubric(), locale, model));
    private static GatekeeperOptions Options(CalibrationReport report, bool reuse = false) => new()
    {
        CalibrationReportStore = new Store(report), CalibrationReusePolicy = reuse ? CalibrationReusePolicy.AllowSameAxis : CalibrationReusePolicy.RequireMatchingInstrument
    };
    private static async Task<CalibrationReport> Calibrate(IChatGate judge, string axis = "over-refusal", bool assertReady = true)
    {
        var report = await GateCalibrationHarness.EvaluateAsync(judge, new(axis, [new("CASE_BLOCK", true), new("CASE_ALLOW", false)]),
            new() { MaxConcurrency = 1, MinCasesPerDirection = 1, MinDecisiveAccuracy = 1 });
        Assert.Equal(assertReady, report.IsInlineReady);
        return report;
    }
    private sealed class LegacyGate(string axis = "over-refusal") : IChatGate, IRequiresCalibration
    {
        public string AxisName => axis;
        public string PolicyName => "legacy";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(text.Contains("CASE_BLOCK", StringComparison.Ordinal) ? GateVerdict.Block(PolicyName, "block") : GateVerdict.Allow(PolicyName));
    }
    private sealed class LegacyApproval : IToolApprovalGate, IRequiresCalibration
    {
        public string AxisName => "over-refusal";
        public string PolicyName => "same-approval-name";
        public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
    private sealed class Store(CalibrationReport report) : ICalibrationReportStore
    {
        public Task SaveAsync(CalibrationReport value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CalibrationReport?> LoadLatestAsync(string axis, CancellationToken ct = default) => Task.FromResult<CalibrationReport?>(report);
        public Task<IReadOnlyDictionary<string, CalibrationReport>> LoadLatestAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Client : IChatClient
    {
        internal string? Model { get; set; } = "observed-model";
        internal string? Reply { get; set; }
        internal int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var reply = Reply ?? (messages.Last().Text.Contains("CASE_BLOCK", StringComparison.Ordinal) ? Block : Allow);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = Model });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
