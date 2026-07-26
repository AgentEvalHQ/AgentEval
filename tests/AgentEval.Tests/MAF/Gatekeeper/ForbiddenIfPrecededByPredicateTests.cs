// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.2c — run-scoped prior-proposal contract predicate.</summary>
public class ForbiddenIfPrecededByPredicateTests
{
    [Fact]
    public void Constructor_NormalizesDeduplicatesDefensivelyCopiesAndBounds()
    {
        var triggers = new[] { " Read_Secrets ", "read_secrets", "lookup" };
        var predicate = new ForbiddenIfPrecededByPredicate(triggers);
        triggers[0] = "changed";

        Assert.Equal(2, predicate.TriggerTools.Count);
        Assert.Contains("Read_Secrets", predicate.TriggerTools);
        Assert.DoesNotContain("changed", predicate.TriggerTools);
        Assert.Throws<ArgumentException>(() => new ForbiddenIfPrecededByPredicate([]));
        Assert.Throws<ArgumentException>(() => new ForbiddenIfPrecededByPredicate([" "]));
        Assert.Throws<ArgumentException>(() =>
            new ForbiddenIfPrecededByPredicate([new string('x', 257)]));
        Assert.Throws<ArgumentException>(() =>
            new ForbiddenIfPrecededByPredicate(Enumerable.Range(0, 257).Select(index => $"tool-{index}")));
    }

    [Fact]
    public async Task EarlierTriggerProposal_BlocksGuardedToolCaseInsensitively()
    {
        var gate = Gate("send", "read_secrets");

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("READ_SECRETS"))).Action);
        var verdict = await gate.InspectAsync(Call("SEND"));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("forbiddenIfPrecededBy", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("read_secrets", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argument ''", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedProposal_DoesNotBlock()
    {
        var gate = Gate("send", "read_secrets");

        await gate.InspectAsync(Call("lookup"));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public async Task TriggerIsRecordedBeforeItsOwnContractPredicateBlocks()
    {
        var gate = new ToolUsageContractGate(
        [
            new ToolContract("read_secrets", [new PiiPredicate("body")]),
            new ToolContract("send", [new ForbiddenIfPrecededByPredicate(["read_secrets"])]),
        ]);

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("read_secrets"))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public async Task LaterGateBlockingTrigger_DoesNotEraseProposalHistory()
    {
        var sequenceGate = Gate("send", "read_secrets");
        var laterGate = new ForbiddenToolGate("read_secrets");
        var trigger = Call("read_secrets");

        Assert.Equal(ToolGateAction.Allow, (await sequenceGate.InspectAsync(trigger)).Action);
        Assert.Equal(ToolGateAction.Block, (await laterGate.InspectAsync(trigger)).Action);
        Assert.Equal(ToolGateAction.Block, (await sequenceGate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public async Task StateIsIsolatedBetweenRuns()
    {
        var gate = Gate("send", "read_secrets");
        using (AgentRunScope.Begin(null, "first", null))
        {
            await gate.InspectAsync(Call("read_secrets"));
            Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send"))).Action);
        }

        using (AgentRunScope.Begin(null, "second", null))
        {
            Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
        }
    }

    [Fact]
    public async Task NoScopeFallbackIsStable_AndRequirementDeclaresRunScope()
    {
        var gate = Gate("send", "read_secrets");

        await gate.InspectAsync(Call("read_secrets"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send"))).Action);
        Assert.Equal(GateRequirements.RunScope, gate.Requirements);
        Assert.Equal(GateCost.PureCode, gate.Cost);
    }

    [Fact]
    public async Task ToolThatIsBothTriggerAndGuarded_AllowsFirstAndBlocksSecond()
    {
        var gate = Gate("send", "send");

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public async Task GuardInspectedBeforeSameBatchTriggerAllows_ThenLaterGuardBlocks()
    {
        var gate = Gate("send", "read_secrets");

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("read_secrets"))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public async Task ConcurrentInspectionIsThreadSafe()
    {
        var gate = Gate("send", "read_secrets");
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => gate.InspectAsync(Call("read_secrets")).AsTask()));

        var verdicts = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => gate.InspectAsync(Call("send")).AsTask()));

        Assert.All(verdicts, verdict => Assert.Equal(ToolGateAction.Block, verdict.Action));
    }

    [Fact]
    public async Task CancellationBeforeObservation_DoesNotRecordTrigger()
    {
        var gate = Gate("send", "read_secrets");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.InspectAsync(Call("read_secrets"), cancellation.Token));

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send"))).Action);
    }

    [Fact]
    public void FluentBuilder_ProducesOperationalSequencePredicate()
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Contract("send", builder => builder.ForbiddenIfPrecededBy("read_secrets"));
            captured = options;
        });

        var gate = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        Assert.IsType<ForbiddenIfPrecededByPredicate>(gate.Contracts[0].Predicates.Single());
    }

    [Fact]
    public void FingerprintIsOrderAndCaseStable_DistinguishesTriggerSets_AndDoesNotContainNames()
    {
        var first = Gate("send", "read_secrets", "lookup");
        var equivalent = Gate("SEND", " LOOKUP ", "READ_SECRETS");
        var different = Gate("send", "other");

        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
        Assert.DoesNotContain("read_secrets", first.ConfigurationFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JsonConfiguration_HasFluentParityAndIsOperational()
    {
        const string json = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "send",
                "predicates": [{
                  "kind": "forbiddenIfPrecededBy",
                  "triggerTools": ["lookup", "READ_SECRETS"]
                }]
              }]
            }
            """;
        var parsed = ResolveJson(json);
        var fluent = Gate("SEND", "read_secrets", "LOOKUP");

        Assert.Equal(fluent.ConfigurationFingerprint, parsed.ConfigurationFingerprint);
        await parsed.InspectAsync(Call("lookup"));
        Assert.Equal(ToolGateAction.Block, (await parsed.InspectAsync(Call("send"))).Action);
    }

    [Theory]
    [InlineData("""{"kind":"forbiddenIfPrecededBy","triggerTools":[]}""", "trigger_count_limit")]
    [InlineData("""{"kind":"forbiddenIfPrecededBy","triggerTools":[7]}""", "trigger_type")]
    [InlineData("""{"kind":"forbiddenIfPrecededBy","triggerTools":[" "]}""", "empty_trigger")]
    [InlineData("""{"kind":"forbiddenIfPrecededBy","triggerTools":["read"],"argument":"body"}""", "unknown_predicate_property")]
    public void JsonConfiguration_RejectsInvalidShapesWithoutEchoingValues(string predicate, string code)
    {
        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(Document(predicate)));

        Assert.Equal(code, exception.ErrorCode);
        Assert.DoesNotContain("read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLimits_RejectTooManyAndOverlongTriggers()
    {
        var tooMany = string.Join(",", Enumerable.Range(0, 257).Select(index => $"\"tool-{index}\""));
        var tooLong = new string('x', 257);

        Assert.Equal(
            "trigger_count_limit",
            Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromJson(
                    Document($$"""{"kind":"forbiddenIfPrecededBy","triggerTools":[{{tooMany}}]}"""))).ErrorCode);
        Assert.Equal(
            "trigger_length_limit",
            Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromJson(
                    Document($$"""{"kind":"forbiddenIfPrecededBy","triggerTools":["{{tooLong}}"]}"""))).ErrorCode);
    }

    [Fact]
    public void EmbeddedSchema_AcceptsSequenceShapeAndRejectsArgument()
    {
        var schema = LoadSchema();
        var valid = JsonNode.Parse(Document(
            """{"kind":"forbiddenIfPrecededBy","triggerTools":["read_secrets"]}"""));
        var invalid = JsonNode.Parse(Document(
            """{"kind":"forbiddenIfPrecededBy","triggerTools":["read_secrets"],"argument":"body"}"""));

        Assert.True(schema.Evaluate(valid, new EvaluationOptions()).IsValid);
        Assert.False(schema.Evaluate(invalid, new EvaluationOptions()).IsValid);
    }

    private static ToolUsageContractGate Gate(string guardedTool, params string[] triggers)
        => new([new ToolContract(guardedTool, [new ForbiddenIfPrecededByPredicate(triggers)])]);

    private static GatedToolCall Call(string tool)
        => new(tool, null, "agent", 0, 0, 1, false, null);

    private static string Document(string predicate)
        => $$"""{"schema":"gatekeeper.contract/1","contracts":[{"tool":"send","predicates":[{{predicate}}]}]}""";

    private static ToolUsageContractGate ResolveJson(string json)
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.LoadContractsFromJson(json);
            captured = options;
        });
        return Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
    }

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "sequence-contract-test" });

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(ToolUsageContractGate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".gatekeeper-contract-v1.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
