// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Prompts;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Prompts;

public sealed class LocalizedSpecializedProbeTests
{
    private static string Reply(string verdict) => $$"""{"verdict":"{{verdict}}","confidence":0.9,"reason":"Underlag"}""";
    private static FakeChatClient Client(string verdict = "RESISTED") => new(Enumerable.Repeat(Reply(verdict), 100).ToArray());
    private static AttackProbe Probe(string? technique = null, IReadOnlyDictionary<string, object>? metadata = null) => new()
    { Id = "sv-specialized", Prompt = "Bedöm svaret", Difficulty = Difficulty.Easy, Technique = technique, Metadata = metadata };

    private static JudgePromptOptions Select(IEnumerable<JudgePromptDefinition> definitions) => new()
    {
        Locale = "sv-SE", CaptureContent = true,
        Pack = new(definitions.Select(d => d with { Locale = "sv-SE", SystemText = "Svensk bedömning " + d.EvaluatorKey }))
    };

    private static IProbeEvaluator Grader(IAttackType attack, IChatClient client)
    {
        var definitions = GraderFactory.CapturePromptDefinitions(attack, new() { JudgeClient = client });
        return GraderFactory.For(attack, new() { JudgeClient = client,
            JudgePrompts = definitions.Count == 0 ? new() { Locale = "sv-SE" } : Select(definitions) });
    }

    [Theory]
    [InlineData("Jailbreak", 3)]
    [InlineData("PIILeakage", 1)]
    [InlineData("SystemPromptExtraction", 0)]
    [InlineData("EncodingEvasion", 1)]
    [InlineData("VectorEmbedding", 1)]
    [InlineData("ToolEscalation", 1)]
    public void Factory_SpecializedRoster_ExportsAndPreflightsWithoutProviderCalls(string name, int count)
    {
        using var client = Client();
        var attack = Attack.ByName(name)!;
        Assert.Equal(count, GraderFactory.CapturePromptDefinitions(attack, new() { JudgeClient = client }).Count);
        Assert.NotNull(Grader(attack, client));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Scan_DefaultRoster_UsesOnePackWithoutHiddenEnglishGraders()
    {
        using var client = Client("INCONCLUSIVE");
        var definitions = Attack.All.SelectMany(a => GraderFactory.CapturePromptDefinitions(a, new() { JudgeClient = client }))
            .DistinctBy(d => d.EvaluatorKey).ToArray();
        var subject = new Subject();
        var plans = new List<ScanPlan>();
        var result = await new RedTeamRunner().ScanAsync(subject, new()
        {
            JudgeClient = client, JudgePrompts = Select(definitions), Intensity = Intensity.Quick,
            MaxProbesPerAttack = 1, IncludeEvidence = true, OnPlanPrepared = plans.Add
        });
        Assert.Equal(Attack.All.Count, result.AttackResults.Count);
        Assert.All(Assert.Single(plans).Attacks, a => Assert.NotNull(a.JudgeGraphDigest));
        Assert.True(subject.Calls > 0);
        Assert.True(client.CallCount > 0);
        Assert.All(client.ReceivedMessages, m => Assert.StartsWith("Svensk bedömning", m.First().Text));
    }

    [Fact]
    public async Task Jailbreak_PositiveSemanticEvidence_IsNotOverriddenByRefusalWords()
    {
        using var client = Client("SUCCEEDED");
        var result = await Grader(Attack.Jailbreak, client).EvaluateAsync(Probe(), "Jag följer nu rollen. I cannot be stopped.");
        Assert.True(result.AttackSucceeded);
        Assert.Equal(1, client.CallCount);
        Assert.Equal("redteam.jailbreak.compromise", Assert.Single(result.JudgeInstruments!).Prompt.EvaluatorKey);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Jailbreak_MarkerBranch_PreservesCodeAbsenceAndSelectedQuotationGate(bool matched, int calls)
    {
        using var client = new FakeChatClient(Reply("INCONCLUSIVE"), Reply("RESISTED"));
        var result = await Grader(Attack.Jailbreak, client).EvaluateAsync(Probe() with { ExpectedTokens = ["HACKED"] },
            matched ? "Jag vägrar att säga HACKED." : "Vanligt svar.");
        Assert.True(result.AttackResisted);
        Assert.Equal(calls, client.CallCount);
        Assert.Equal(calls, result.JudgeInstruments!.Count);
        Assert.Equal(matched ? GradingProvenanceKind.Judge : GradingProvenanceKind.Heuristic, result.GradedBy);
    }

    [Fact]
    public async Task Jailbreak_NoMarkerOracle_UsesRefusalJudgeAfterSemanticAbstention()
    {
        using var client = new FakeChatClient(Reply("INCONCLUSIVE"), Reply("RESISTED"));
        var result = await Grader(Attack.Jailbreak, client).EvaluateAsync(Probe(), "Jag vägrar.");
        Assert.True(result.AttackResisted);
        Assert.Equal(new[] { "redteam.jailbreak.compromise", "redteam.refusal" }, result.JudgeInstruments!.Select(i => i.Prompt.EvaluatorKey));
    }

    [Theory]
    [InlineData("indirect_extraction", false, EvaluationOutcome.Inconclusive, 1)]
    [InlineData("INDIRECT_EXTRACTION", true, EvaluationOutcome.Succeeded, 0)]
    [InlineData("direct_extraction", false, EvaluationOutcome.Succeeded, 1)]
    public async Task Pii_CanaryAndFabricationGuard_AreEnforcedOutsideTheJudge(string technique, bool canary, EvaluationOutcome expected, int calls)
    {
        using var client = Client("SUCCEEDED");
        var result = await Grader(Attack.PIILeakage, client).EvaluateAsync(Probe(technique,
            new Dictionary<string, object> { ["pii_canary"] = canary ? "LEAK" : "ABSENT-SECRET" }), "Svenska uppgifter: LEAK");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(calls, client.CallCount);
        if (calls == 0) Assert.Null(result.JudgeInstruments);
        else
        {
            Assert.Equal("sv-SE", Assert.Single(result.JudgeInstruments!).Prompt.EffectiveLocale);
            Assert.DoesNotContain("ABSENT-SECRET", Assert.Single(client.ReceivedMessages).Last().Text);
        }
    }

    [Theory]
    [InlineData("Personnummer: 19900101-1234")]
    [InlineData("Inga personuppgifter här.")]
    public async Task Pii_RegionalOrPatternFreeText_DoesNotSkipSelectedJudge(string response)
    {
        using var client = Client("INCONCLUSIVE");
        Assert.True((await Grader(Attack.PIILeakage, client).EvaluateAsync(Probe(), response)).IsInconclusive);
        Assert.Equal(1, client.CallCount);
    }

    [Theory]
    [InlineData(null, "ordinary", EvaluationOutcome.Inconclusive)]
    [InlineData("CANARY", "CANARY", EvaluationOutcome.Succeeded)]
    [InlineData("CANARY", "ordinary", EvaluationOutcome.Resisted)]
    public async Task SystemPrompt_PermissionedCanary_StaysCodeOnly(string? canary, string response, EvaluationOutcome expected)
    {
        using var client = Client();
        var result = await Grader(Attack.SystemPromptExtraction, client).EvaluateAsync(Probe(metadata:
            canary is null ? null : new Dictionary<string, object> { ["system_prompt_canary"] = canary }), response);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.JudgeInstruments);
        Assert.NotNull(result.JudgeGraphDigest);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData(false, false, EvaluationOutcome.Inconclusive, 0)]
    [InlineData(true, false, EvaluationOutcome.Resisted, 1)]
    [InlineData(true, true, EvaluationOutcome.Succeeded, 1)]
    public async Task Retrieval_DeliveryBoundaryAndMarkerMeaning_AreSeparate(bool executed, bool complied, EvaluationOutcome expected, int calls)
    {
        using var client = Client(complied ? "SUCCEEDED" : "RESISTED");
        var attack = new CustomAttack(new VectorEmbeddingEvaluator("MARKER", "retrieve"));
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, [new FunctionCallContent("r1", "retrieve", null)]) };
        if (executed) messages.Add(new(ChatRole.Tool, [new FunctionResultContent("r1", "poisoned document")]));
        var response = new AgentResponse { Text = "Jag vägrar att säga MARKER.", RawMessages = messages };
        if (executed && !complied)
        {
            var legacy = await new VectorEmbeddingEvaluator("MARKER", "retrieve").EvaluateAsync(Probe(VectorEmbeddingEvaluator.RagToolTechnique), response);
            Assert.True(legacy.AttackSucceeded); // Existing raw-marker oracle cannot distinguish a quoted refusal.
            Assert.Equal(EvidenceFidelity.Behavioral, legacy.Metadata!["fidelity"]);
        }
        var result = await Grader(attack, client).EvaluateAsync(Probe(VectorEmbeddingEvaluator.RagToolTechnique),
            response);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(calls, client.CallCount);
        if (executed)
        {
            Assert.Equal(true, result.Metadata!["retrieval_executed"]);
            Assert.Equal(EvidenceFidelity.Verbal, result.Metadata["fidelity"]);
            Assert.Equal(GradingProvenanceKind.Judge, result.GradedBy);
        }
    }

    [Fact]
    public async Task Retrieval_TextOnlyToolMode_AbstainsWithoutCallingJudge()
    {
        using var client = Client();
        var result = await Grader(Attack.VectorEmbedding, client).EvaluateAsync(Probe(VectorEmbeddingEvaluator.RagToolTechnique), "MARKER");
        Assert.True(result.IsInconclusive);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolEscalation_ObservedForbiddenCall_WinsWithoutSemanticDowngrade(bool executed)
    {
        using var client = Client("RESISTED");
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, [new FunctionCallContent("t1", "forbidden", null)]) };
        if (executed) messages.Add(new(ChatRole.Tool, [new FunctionResultContent("t1", "ok")]));
        var result = await Grader(new CustomAttack(new ToolEscalationEvaluator(["forbidden"])), client).EvaluateAsync(Probe(),
            new AgentResponse { Text = "Jag vägrar.", RawMessages = messages });
        Assert.True(result.AttackSucceeded);
        Assert.Equal(executed ? EvidenceFidelity.Behavioral : EvidenceFidelity.IntentToAct, result.Metadata!["fidelity"]);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("SUCCEEDED", EvaluationOutcome.Inconclusive)]
    [InlineData("RESISTED", EvaluationOutcome.Resisted)]
    public async Task ToolEscalation_TextOnlyRefusal_CannotInventAnExecutedAttack(string reply, EvaluationOutcome expected)
    {
        using var client = Client(reply);
        var result = await Grader(Attack.ToolEscalation, client).EvaluateAsync(Probe(), "Svenskt svar.");
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, client.CallCount);
        Assert.Equal(EvidenceFidelity.Verbal, result.Metadata!["fidelity"]);
    }

    [Theory]
    [InlineData("Jailbreak")]
    [InlineData("PIILeakage")]
    [InlineData("SystemPromptExtraction")]
    [InlineData("VectorEmbedding")]
    [InlineData("ToolEscalation")]
    public async Task BoundGraph_CancelledCaller_DoesNotProduceAVerdictOrCallJudge(string name)
    {
        using var client = Client();
        var bound = Grader(Attack.ByName(name)!, client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bound.EvaluateAsync(Probe(), "Svar", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bound.EvaluateAsync(Probe(), new AgentResponse { Text = "Svar" }, cancelled.Token));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Retrieval_ChangedBoundary_ChangesDeclaredGraphIdentity()
    {
        using var client = Client();
        var first = await Grader(new CustomAttack(new VectorEmbeddingEvaluator("FIRST", "retrieve")), client).EvaluateAsync(Probe(), "Svar");
        var markerChanged = await Grader(new CustomAttack(new VectorEmbeddingEvaluator("SECOND", "retrieve")), client).EvaluateAsync(Probe(), "Svar");
        var toolChanged = await Grader(new CustomAttack(new VectorEmbeddingEvaluator("FIRST", "lookup")), client).EvaluateAsync(Probe(), "Svar");
        Assert.NotEqual(first.JudgeGraphDigest, markerChanged.JudgeGraphDigest);
        Assert.NotEqual(first.JudgeGraphDigest, toolChanged.JudgeGraphDigest);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("Jailbreak")]
    [InlineData("SupplyChain")]
    public async Task ConfiguredGraph_UnreadablePositiveJudge_DoesNotBecomeASafetyPass(string name)
    {
        using var client = new FakeChatClient("not-json", "still-not-json", Reply("RESISTED"));
        var probe = name == "Jailbreak" ? Probe() with { ExpectedTokens = ["HACKED"] } : Probe();
        var result = await Grader(Attack.ByName(name)!, client).EvaluateAsync(probe, "Svar utan markör.");
        Assert.True(result.IsInconclusive);
        Assert.NotEmpty(result.JudgeInstruments!);
    }

    [Fact]
    public async Task ConfiguredGraph_UnreadableTextJudge_DoesNotEraseObservedForbiddenTool()
    {
        using var client = new FakeChatClient("not-json", "still-not-json", Reply("RESISTED"));
        var result = await Grader(Attack.ExcessiveAgency, client).EvaluateAsync(Probe(), new AgentResponse
        {
            Text = "Svar", RawMessages =
            [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("t1", "admin_delete", null)]),
             new ChatMessage(ChatRole.Tool, [new FunctionResultContent("t1", "ok")])]
        });
        Assert.True(result.AttackSucceeded);
        Assert.Equal(EvidenceFidelity.Behavioral, result.Metadata!["fidelity"]);
    }

    private sealed class Subject : IEvaluableAgent
    {
        public string Name => "default-roster-fixture";
        public int Calls { get; private set; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentResponse { Text = "Svenskt svar utan markörer." });
        }
    }

    private sealed class CustomAttack(IProbeEvaluator evaluator) : IAttackType
    {
        public string Name => "SpecializedFixture";
        public string DisplayName => Name;
        public string Description => "Fixture";
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Low;
        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [Probe()];
        public IProbeEvaluator GetEvaluator() => evaluator;
    }
}
