// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.Concurrent;
using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.Gatekeeper;
using AgentEval.Guardrails.Judges;
using AgentEval.Prompts;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Gatekeeper;

public sealed class LocalizedGatekeeperCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gate-localized-" + Guid.NewGuid().ToString("N"));
    private const string Refusal = "Avböjer den ofarliga frågan.";
    private const string Helpful = "Stockholm är Sveriges huvudstad.";
    private const string Fingerprint = "fixture:model@123";

    [Fact]
    public async Task Export_GatekeeperFamily_ReloadsAllTenActualDescriptors()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await PromptsCommand.ExportAsync(_directory, null, output, error, family: "gatekeeper"));
        var pack = await new JudgePromptPackLoader().LoadAsync(Path.Combine(_directory, "prompts.json"));
        Assert.Equal(10, pack.Definitions.Count);
        Assert.All(pack.Definitions, d => Assert.Equal(JudgeResponseContract.GateDecisionV1, d.ResponseContract));
        Assert.Equal(0, await PromptsCommand.InspectAsync(Path.Combine(_directory, "prompts.json"), true, output, error, family: "gatekeeper"));
        Assert.Equal(0, await PromptsCommand.InspectAsync(Path.Combine(_directory, "prompts.json"), true, output, error));
    }

    [Fact]
    public async Task Calibrate_ThenInspect_BindsDatasetPromptAndObservedModel()
    {
        var prompts = Prompts("over-refusal"); var dataset = Dataset(reviewed: true);
        var model = new Model();
        Assert.Equal(0, await Calibrate(model, prompts, dataset));
        Assert.Equal(2, model.Calls.Count);
        Assert.All(model.Calls, c => Assert.Contains("Bedöm", c.System));
        var (exit, json, _) = await Inspect(model, prompts, dataset);
        Assert.Equal(ExitCodes.GateBlocked, exit);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("inlineReady").GetBoolean());
        var instrument = document.RootElement.GetProperty("judgeInstruments")[0];
        Assert.Equal("sv", instrument.GetProperty("prompt").GetProperty("effectiveLocale").GetString());
        Assert.Equal("fixture-model", instrument.GetProperty("calls")[0].GetProperty("modelId").GetString());
        Assert.Equal(3, model.Calls.Count);

        var changed = Prompts("over-refusal", version: "2.0.0");
        Assert.Equal(ExitCodes.NotCertified, (await Inspect(model, changed, dataset)).Exit);
        Assert.Equal(ExitCodes.NotCertified, (await Inspect(model, prompts, dataset with { Id = "changed-dataset" })).Exit);
        Assert.Equal(ExitCodes.NotCertified, (await Inspect(model, prompts, dataset, requestedModel: "another-model")).Exit);
        Assert.Equal(3, model.Calls.Count); // Every stale identity is rejected before a dispatch.
    }

    [Fact]
    public async Task Calibrate_AiAuthoredUnreviewedLabels_RemainDiagnosticDespitePerfectAgreement()
    {
        var dataset = Dataset(reviewed: false); var prompts = Prompts("over-refusal");
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(ExitCodes.TestFailure, await GatekeeperCalibrateCommand.RunAsync("judge:over-refusal", new Model(),
            Fingerprint, 0, 1, 1, true, null, _directory, output, error, default, Options(prompts), dataset));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.True(document.RootElement.GetProperty("isInlineReady").GetBoolean()); // Statistical library bar only.
        Assert.False(document.RootElement.GetProperty("certificateEligible").GetBoolean());
        var certificate = InlineReadinessStore.TryLoad("over-refusal", Fingerprint, _directory, Digest(prompts), dataset.Digest);
        Assert.NotNull(certificate);
        Assert.False(certificate.IsInlineReady);
        Assert.Equal("ai-authored", certificate.Dataset!.LabelOrigin);
        Assert.Null(certificate.Dataset.Reviewer);
    }

    [Theory]
    [InlineData("training")]
    [InlineData("full")]
    public async Task Calibrate_ReviewedNonHeldOutSplit_CannotCertify(string split)
    {
        Assert.Equal(ExitCodes.TestFailure, await Calibrate(new Model(), Prompts("over-refusal"), Dataset(true) with { SplitLabel = split }));
    }

    [Fact]
    public async Task Calibrate_InvalidReplies_RetainsUnmeasuredCasesAndCannotCertify()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var model = new Model { Reply = "not-json" };
        Assert.Equal(ExitCodes.TestFailure, await GatekeeperCalibrateCommand.RunAsync("judge:over-refusal", model,
            Fingerprint, 0, 1, 1, false, null, _directory, output, error, default, Options(Prompts("over-refusal")), Dataset(true)));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, document.RootElement.GetProperty("unmeasuredCases").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("total").GetInt32());
        Assert.False(document.RootElement.GetProperty("isInlineReady").GetBoolean());
        Assert.Equal(4, model.Calls.Count);
        Assert.All(document.RootElement.GetProperty("cases").EnumerateArray(), c => Assert.False(c.GetProperty("measured").GetBoolean()));
    }

    [Fact]
    public async Task Inspect_ObservedModelChanged_DoesNotRetainInlineReadiness()
    {
        var prompts = Prompts("over-refusal"); var dataset = Dataset(true);
        Assert.Equal(0, await Calibrate(new Model(), prompts, dataset));
        var result = await Inspect(new Model { ObservedModel = "replacement-model" }, prompts, dataset);
        using var document = JsonDocument.Parse(result.Json);
        Assert.False(document.RootElement.GetProperty("inlineReady").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("certificate").ValueKind);
        Assert.Contains("calibration", document.RootElement.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task Inspect_LegacyCertificate_CannotApproveConfiguredJudge()
    {
        var gold = JudgeAxisRegistry.For("over-refusal")!.GoldSet();
        InlineReadinessStore.Save(new("over-refusal", Fingerprint, InlineReadinessStore.GoldSetHash(gold), true,
            "fixture", new CalibrationReportDto { IsInlineReady = true }), dir: _directory);
        var model = new Model();
        Assert.Equal(ExitCodes.NotCertified, (await Inspect(model, Prompts("over-refusal"), null)).Exit);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task Inspect_ModelReply_UsesConfiguredReaderWithoutFabricatedProviderCalls()
    {
        Directory.CreateDirectory(_directory);
        var reply = Path.Combine(_directory, "reply.json");
        await File.WriteAllTextAsync(reply, Model.Blocked);
        var args = Args(Prompts("over-refusal"), null, new Model()) with { ModelReply = reply, ResolvedModel = null };
        var result = await InspectArgs("judge:over-refusal", args);
        Assert.Equal(ExitCodes.GateBlocked, result.Exit);
        using var document = JsonDocument.Parse(result.Json);
        Assert.False(document.RootElement.GetProperty("inlineReady").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("judgeInstruments")[0].GetProperty("calls").EnumerateArray());
        await File.WriteAllTextAsync(reply, "not-json");
        var invalid = await InspectArgs("judge:over-refusal", args);
        Assert.Equal(ExitCodes.GateInconclusive, invalid.Exit);
    }

    [Theory]
    [InlineData("panel:judge:over-refusal,judge:exfiltration-intent")]
    [InlineData("panel:judge:over-refusal,no-such-gate")]
    [InlineData("keyword-injection")]
    public async Task Inspect_InvalidPanelOrCodeOnlySelection_FailsBeforeAnyCalls(string gate)
    {
        var model = new Model();
        var result = await InspectArgs(gate, Args(Prompts("over-refusal"), null, model) with { AllowUncalibrated = true });
        Assert.Equal(ExitCodes.UsageError, result.Exit);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task Inspect_PanelUnmeasuredChild_RemainsInconclusiveAndRetainsObservations()
    {
        var model = new Model { Reply = "invalid" };
        var result = await InspectArgs("panel:keyword-injection,judge:over-refusal",
            Args(Prompts("over-refusal"), null, model) with { AllowUncalibrated = true });
        Assert.Equal(ExitCodes.GateInconclusive, result.Exit);
        using var document = JsonDocument.Parse(result.Json);
        Assert.True(document.RootElement.GetProperty("inconclusive").GetBoolean());
        Assert.False(document.RootElement.GetProperty("judgeInstruments")[0].GetProperty("measured").GetBoolean());
        Assert.Equal(2, model.Calls.Count);
    }

    [Fact]
    public async Task Inspect_PanelCertifiedPerAxis_UsesEachDatasetAndRetainsBothInstruments()
    {
        var prompts = Prompts("over-refusal", "exfiltration-intent");
        var datasets = new[] { Dataset(true), Dataset(true) with { Axis = "exfiltration-intent" } };
        foreach (var data in datasets)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Equal(0, await GatekeeperCalibrateCommand.RunAsync("judge:" + data.Axis, new Model(), Fingerprint,
                0, 1, 1, true, null, _directory, output, error, default, Options(prompts), data));
        }
        var result = await InspectArgs("panel:judge:over-refusal,judge:exfiltration-intent",
            Args(prompts, null, new Model()) with { Datasets = datasets });
        Assert.Equal(ExitCodes.GateBlocked, result.Exit);
        using var document = JsonDocument.Parse(result.Json);
        Assert.True(document.RootElement.GetProperty("inlineReady").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("judgeInstruments").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("certificate").GetArrayLength());
    }

    [Fact]
    public async Task Inspect_AllRegisteredAxes_ForwardPromptOptionsThroughTheirActualFactories()
    {
        foreach (var axis in JudgeAxisRegistry.Axes)
        {
            var model = new Model();
            var result = await InspectArgs("judge:" + axis, Args(Prompts(axis), null, model) with { AllowUncalibrated = true });
            Assert.Equal(ExitCodes.GateBlocked, result.Exit);
            Assert.Single(model.Calls);
            using var document = JsonDocument.Parse(result.Json);
            Assert.Single(document.RootElement.GetProperty("judgeInstruments").EnumerateArray());
        }
    }

    [Fact]
    public void Dataset_ValidFile_RoundTripsAndRejectsMissingBooleanLabel()
    {
        Directory.CreateDirectory(_directory); var path = Path.Combine(_directory, "data.json");
        var data = Dataset(false);
        var node = JsonSerializer.SerializeToNode(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        File.WriteAllText(path, node.ToJsonString());
        Assert.Equal(data.Digest, GateCalibrationDataset.Load(path)!.Digest);
        node["cases"]![0]!.AsObject().Remove("shouldBlock");
        File.WriteAllText(path, node.ToJsonString());
        Assert.Throws<ArgumentException>(() => GateCalibrationDataset.Load(path));
    }

    [Fact]
    public void CommandOptions_PromptSelectionAndRepeatedDatasetFlags_AreAccepted()
    {
        Assert.Empty(GatekeeperInspectCommand.Create().Parse(["--gate", "panel:judge:over-refusal,judge:exfiltration-intent",
            "--judge-prompt-pack", "prompts.json", "--judge-locale", "sv", "--gold-set", "one.json", "two.json"]).Errors);
        Assert.Empty(GatekeeperCalibrateCommand.Create().Parse(["--gate", "judge:over-refusal",
            "--judge-prompt-pack", "prompts.json", "--judge-locale", "sv", "--gold-set", "one.json"]).Errors);
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("temperature")]
    [InlineData("output")]
    [InlineData("input")]
    public async Task Inspect_ChangedExecutionSettings_RejectsPriorCertificateBeforeCalling(string setting)
    {
        var prompts = Prompts("over-refusal"); var data = Dataset(true); var model = new Model();
        Assert.Equal(0, await Calibrate(model, prompts, data));
        var settings = setting switch
        {
            "deadline" => new GateRuntimeSettings(TimeoutSeconds: 45),
            "temperature" => new GateRuntimeSettings(Temperature: 0),
            "output" => new GateRuntimeSettings(MaxOutputTokens: 4096),
            _ => new GateRuntimeSettings(MaxInputChars: 8000)
        };
        Assert.Equal(ExitCodes.NotCertified, (await InspectArgs("judge:over-refusal", Args(prompts, data, model) with { RuntimeSettings = settings })).Exit);
        Assert.Equal(2, model.Calls.Count);
    }

    [Fact]
    public void RuntimeSettings_UnconfiguredOverrideOrInvalidTimeout_RejectsAmbiguousIdentity()
    {
        Assert.Throws<ArgumentException>(() => new GateRuntimeSettings(TimeoutSeconds: 45).Bind(null, "fixture"));
        Assert.Throws<ArgumentException>(() => new GateRuntimeSettings(TimeoutSeconds: double.NaN).Bind(Prompts("over-refusal"), "fixture"));
        Assert.Throws<ArgumentException>(() => new GateRuntimeSettings(TimeoutSeconds: -1).Bind(Prompts("over-refusal"), "fixture"));
    }

    [Fact]
    public async Task Inspect_CorruptCertificateInstrumentList_RejectsBeforeCalling()
    {
        var prompts = Prompts("over-refusal"); var data = Dataset(true); var model = new Model();
        Assert.Equal(0, await Calibrate(model, prompts, data));
        var file = InlineReadinessStore.PathFor("over-refusal", Fingerprint, _directory, Digest(prompts), data.Digest);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["report"]!["instrumentDigests"] = null;
        await File.WriteAllTextAsync(file, json.ToJsonString());
        Assert.Equal(ExitCodes.NotCertified, (await Inspect(model, prompts, data)).Exit);
        Assert.Equal(2, model.Calls.Count);
    }

    [Theory]
    [InlineData("judge:exfiltration-intent")]
    [InlineData("panel:judge:exfiltration-intent,keyword-injection")]
    public async Task Inspect_SensitiveConfiguredRationaleAndCapturedContent_AreNotEmitted(string gate)
    {
        var sensitive = await InspectArgs(gate,
            Args(Prompts("exfiltration-intent") with { CaptureContent = true }, null,
                new Model { Reply = "{\"decision\":\"BLOCKED\",\"confidence\":1,\"rationale\":\"SECRET_CANARY\",\"evidence\":[\"SECRET_CANARY\"]}" })
            with { AllowUncalibrated = true });
        Assert.Equal(ExitCodes.GateBlocked, sensitive.Exit);
        Assert.DoesNotContain("SECRET_CANARY", sensitive.Json);
        Assert.DoesNotContain("userMessage", sensitive.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"id\":\"missing-label\"}")]
    [InlineData("{\"id\":\"one\",\"id\":\"two\"}")]
    public void Dataset_MissingOrDuplicateFields_FailsClosed(string json)
    {
        Directory.CreateDirectory(_directory); var file = Path.Combine(_directory, "data.json"); File.WriteAllText(file, json);
        Assert.Throws<ArgumentException>(() => GateCalibrationDataset.Load(file));
    }

    [Fact]
    public void Dataset_ChangedLabelOrReviewMetadata_ChangesIdentity()
    {
        var original = Dataset(false);
        Assert.NotEqual(original.Digest, (original with { Reviewer = "claimed-reviewer" }).Digest);
        Assert.NotEqual(original.Digest, (original with { Cases = [new() { Text = Refusal, ShouldBlock = false }, new() { Text = Helpful, ShouldBlock = true }] }).Digest);
        Assert.Throws<ArgumentException>(() => (original with { ReviewStatus = "human-reviewed" }).Validate("over-refusal"));
    }

    private async Task<int> Calibrate(Model model, JudgePromptOptions prompts, GateCalibrationDataset dataset)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        return await GatekeeperCalibrateCommand.RunAsync("judge:over-refusal", model, Fingerprint, 0, 1, 1,
            true, null, _directory, output, error, default, Options(prompts), dataset);
    }

    private Task<(int Exit, string Json, string Error)> Inspect(Model model, JudgePromptOptions prompts, GateCalibrationDataset? dataset, string requestedModel = "fixture-model") =>
        InspectArgs("judge:over-refusal", Args(prompts, dataset, model) with { ResolvedModel = new(model, Fingerprint, 0, requestedModel) });

    private JudgeArgs Args(JudgePromptOptions prompts, GateCalibrationDataset? dataset, Model model) =>
        new(false, null, null, "fixture-model", null, null, null, false, _directory, prompts, dataset)
        { ResolvedModel = new(model, Fingerprint, 0, "fixture-model") };

    private static async Task<(int Exit, string Json, string Error)> InspectArgs(string gate, JudgeArgs args)
    {
        using var input = new StringReader(JsonSerializer.Serialize(new { text = Refusal }));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await GatekeeperInspectCommand.RunAsync(gate, null, "block", new(), input, output, error, default, args);
        return (exit, output.ToString(), error.ToString());
    }

    private static GateCalibrationDataset Dataset(bool reviewed) => new()
    {
        Id = "test-fixture", Axis = "over-refusal", Locale = "sv", SplitLabel = "held-out", LabelOrigin = "ai-authored",
        ReviewStatus = reviewed ? "human-reviewed" : "unreviewed", Reviewer = reviewed ? "fixture-reviewer (test only)" : null,
        Cases = [new() { Text = Refusal, ShouldBlock = true }, new() { Text = Helpful, ShouldBlock = false }]
    };

    private static JudgePromptOptions Prompts(string axis, string? secondAxis = null, string version = "1.0.0") => new()
    {
        Locale = "sv", Pack = new(new[] { axis, secondAxis }.OfType<string>().Select(a =>
            GateJudgePrompts.ForRubric(JudgeAxisRegistry.For(a)!.Rubric()) with { Locale = "sv", SystemText = "Bedöm det givna underlaget.", Version = version }))
    };
    private static JudgeGateOptions Options(JudgePromptOptions prompts) => new() { JudgePrompts = prompts, ModelId = "fixture-model" };
    private static string Digest(JudgePromptOptions prompts) => ((IRequiresCalibration)JudgeAxisRegistry.For("over-refusal")!.Create(new Model(), false, Options(prompts))).InstrumentDigest!;

    private sealed class Model : IChatClient
    {
        internal const string Blocked = "{\"decision\":\"BLOCKED\",\"confidence\":1,\"rationale\":\"flag\",\"evidence\":[]}";
        internal string? Reply { get; init; }
        internal string? ObservedModel { get; init; } = "fixture-model";
        internal ConcurrentBag<(string System, string User)> Calls { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = messages.ToArray();
            Calls.Add((list[0].Text!, list[1].Text!));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply ??
                (list[1].Text!.Contains(Refusal, StringComparison.Ordinal) ? Blocked : Blocked.Replace("BLOCKED", "ALLOWED")))) { ModelId = ObservedModel });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
