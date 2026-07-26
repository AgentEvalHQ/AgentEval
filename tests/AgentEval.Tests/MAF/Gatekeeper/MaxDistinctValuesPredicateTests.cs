// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.3 — bounded, hashed, atomic distinct-value contract state.</summary>
public class MaxDistinctValuesPredicateTests
{
    [Fact]
    public void ConstructorBoundsMaximum_AndMetadataRequiresRunScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaxDistinctValuesPredicate("value", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaxDistinctValuesPredicate("value", 4097));
        Assert.Equal(1, new MaxDistinctValuesPredicate("value", 1).Max);
        Assert.Equal(4096, new MaxDistinctValuesPredicate("value", 4096).Max);

        var gate = Gate(max: 2);
        Assert.Equal(GateCost.PureCode, gate.Cost);
        Assert.Equal(GateRequirements.RunScope, gate.Requirements);
    }

    [Fact]
    public async Task NewValuesAdmitUpToCap_ExistingValuesRemainAdmissible()
    {
        var gate = Gate(max: 2);
        using var scope = AgentRunScope.Begin(null, "distinct-basic", null);

        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "alpha")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "beta")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "alpha")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, "gamma")).Action);
    }

    [Fact]
    public async Task CanonicalObjectsIgnorePropertyOrder_AndArraysPreserveOrder()
    {
        var objectGate = Gate(max: 1);
        using var scope = AgentRunScope.Begin(null, "distinct-canonical-objects", null);
        var first = new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 };
        var equivalent = JsonDocument.Parse("""{"a":1,"b":2}""").RootElement.Clone();

        Assert.Equal(ToolGateAction.Allow, (await Inspect(objectGate, first)).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(objectGate, equivalent)).Action);

        var arrayGate = Gate("array-tool", max: 1);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(arrayGate, new[] { 1, 2 }, "array-tool")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(arrayGate, new[] { 2, 1 }, "array-tool")).Action);
    }

    [Fact]
    public async Task EquivalentJsonNumbersShareHash_ButJsonTypesRemainDistinct()
    {
        var gate = Gate(max: 1);
        using var scope = AgentRunScope.Begin(null, "distinct-number", null);
        var integer = JsonDocument.Parse("1").RootElement.Clone();
        var decimalForm = JsonDocument.Parse("1.0").RootElement.Clone();
        var exponentForm = JsonDocument.Parse("100e-2").RootElement.Clone();

        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, integer)).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, decimalForm)).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, exponentForm)).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, "1")).Action);
    }

    [Fact]
    public async Task SerializationCanonicalizationAndMissingFailuresBlockWithoutAdmission()
    {
        var gate = Gate(max: 1);
        using var scope = AgentRunScope.Begin(null, "distinct-fail-closed", null);
        var cyclic = new CyclicValue();
        cyclic.Self = cyclic;
        var duplicateProperties = JsonDocument.Parse("""{"a":1,"a":1}""").RootElement.Clone();
        var extremeExponent = JsonDocument.Parse("1e1234567890").RootElement.Clone();

        Assert.Equal(
            ToolGateAction.Block,
            (await Inspect(gate, new string('x', ArgumentCanonicalizer.DefaultMaxLength + 1))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, new string([(char)0xD800]))).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, cyclic)).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, duplicateProperties)).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(gate, extremeExponent)).Action);
        Assert.Equal(ToolGateAction.Block, (await InspectMissing(gate)).Action);
        Assert.Equal(ToolGateAction.Block, (await InspectNull(gate)).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "valid-after-failures")).Action);
    }

    [Fact]
    public async Task StateIsIsolatedByRunConfigurationToolAndArgument()
    {
        var gate = Gate(max: 1);
        using (AgentRunScope.Begin(null, "distinct-run-one", null))
        {
            Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "one")).Action);
            Assert.Equal(ToolGateAction.Block, (await Inspect(gate, "two")).Action);
        }

        using (AgentRunScope.Begin(null, "distinct-run-two", null))
        {
            Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "two")).Action);
        }

        using var isolationScope = AgentRunScope.Begin(null, "distinct-dimensions", null);
        var differentConfiguration = new ToolUsageContractGate([
            new ToolContract("write", [new MaxDistinctValuesPredicate("value", 1)]),
            new ToolContract("unrelated", [new PiiPredicate("text")]),
        ]);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(gate, "config-one")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(differentConfiguration, "config-two")).Action);

        var toolIsolated = new ToolUsageContractGate([
            new ToolContract("tool-a", [new MaxDistinctValuesPredicate("value", 1)]),
            new ToolContract("tool-b", [new MaxDistinctValuesPredicate("value", 1)]),
        ]);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(toolIsolated, "A", "tool-a")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(toolIsolated, "B", "tool-b")).Action);

        var argumentIsolated = new ToolUsageContractGate([
            new ToolContract("pair", [
                new MaxDistinctValuesPredicate("left", 1),
                new MaxDistinctValuesPredicate("right", 1),
            ]),
        ]);
        Assert.Equal(
            ToolGateAction.Allow,
            (await InspectArguments(argumentIsolated, "pair", ("left", "L"), ("right", "R"))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await InspectArguments(argumentIsolated, "pair", ("left", "L"), ("right", "changed"))).Action);
    }

    [Fact]
    public async Task AdmissionIsNotRolledBackWhenALaterPredicateBlocks()
    {
        var gate = new ToolUsageContractGate([
            new ToolContract("write", [
                new MaxDistinctValuesPredicate("value", 1),
                new DeniedKeywordsPredicate("note", ["deny"]),
            ]),
        ]);
        using var scope = AgentRunScope.Begin(null, "distinct-later-block", null);

        Assert.Equal(
            ToolGateAction.Block,
            (await InspectArguments(gate, "write", ("value", "first"), ("note", "deny"))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await InspectArguments(gate, "write", ("value", "second"), ("note", "clean"))).Action);
    }

    [Fact]
    public async Task EarlierPredicateBlockDoesNotReachDistinctAdmission()
    {
        var gate = new ToolUsageContractGate([
            new ToolContract("write", [
                new DeniedKeywordsPredicate("note", ["deny"]),
                new MaxDistinctValuesPredicate("value", 1),
            ]),
        ]);
        using var scope = AgentRunScope.Begin(null, "distinct-earlier-block", null);

        Assert.Equal(
            ToolGateAction.Block,
            (await InspectArguments(gate, "write", ("value", "first"), ("note", "deny"))).Action);
        Assert.Equal(
            ToolGateAction.Allow,
            (await InspectArguments(gate, "write", ("value", "second"), ("note", "clean"))).Action);
    }

    [Fact]
    public async Task ConcurrentAdmissionIsAtomic_AndNeverGrowsPastCap()
    {
        const int limit = 32;
        var gate = Gate(max: limit);
        using var scope = AgentRunScope.Begin(null, "distinct-concurrent", null);

        var firstPass = await Task.WhenAll(Enumerable.Range(0, 512).Select(index =>
            Task.Run(async () => await Inspect(gate, $"value-{index}"))));
        Assert.Equal(limit, firstPass.Count(verdict => verdict.Action == ToolGateAction.Allow));

        var secondPass = await Task.WhenAll(Enumerable.Range(0, 512).Select(index =>
            Task.Run(async () => await Inspect(gate, $"value-{index}"))));
        Assert.Equal(limit, secondPass.Count(verdict => verdict.Action == ToolGateAction.Allow));
    }

    [Fact]
    public void RunLedgerAtomicApiValidatesHashAndBounds_AndIsolatesDimensions()
    {
        var ledger = new RunLedger();
        var first = SHA256.HashData(Encoding.UTF8.GetBytes("first"));
        var second = SHA256.HashData(Encoding.UTF8.GetBytes("second"));

        Assert.Equal(DistinctValueDecision.Admitted, ledger.TryAdmitDistinctValue("dimension-a", first, 1));
        Assert.Equal(DistinctValueDecision.Existing, ledger.TryAdmitDistinctValue("dimension-a", first, 1));
        Assert.Equal(DistinctValueDecision.Exceeded, ledger.TryAdmitDistinctValue("dimension-a", second, 1));
        Assert.Equal(DistinctValueDecision.Admitted, ledger.TryAdmitDistinctValue("dimension-b", second, 1));
        Assert.Throws<ArgumentException>(() => ledger.TryAdmitDistinctValue(" ", first, 1));
        Assert.Throws<ArgumentException>(() => ledger.TryAdmitDistinctValue("dimension", [1, 2], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryAdmitDistinctValue("dimension", first, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryAdmitDistinctValue("dimension", first, 4097));
    }

    [Fact]
    public async Task BlockReasonAndFingerprintDoNotLeakValues()
    {
        const string secret = "SUPER-SECRET-DISTINCT-VALUE";
        var first = Gate(max: 1);
        var equivalent = Gate("WRITE", max: 1);
        var different = Gate(max: 2);
        using var scope = AgentRunScope.Begin(null, "distinct-no-leak", null);

        await Inspect(first, "baseline");
        var verdict = await Inspect(first, secret);

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("maxDistinctValues", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, verdict.Reason!, StringComparison.Ordinal);
        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
        Assert.DoesNotContain(secret, first.ConfigurationFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FluentAndJsonConfigurationHaveFingerprintAndRuntimeParity()
    {
        const string json = """
            {"schema":"gatekeeper.contract/1","contracts":[{"tool":"write","predicates":[
              {"kind":"maxDistinctValues","argument":"value","max":2}
            ]}]}
            """;
        var parsed = ResolveJson(json);
        var fluent = Gate(max: 2);
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Contract("write", contract => contract.MaxDistinctValues("value", 2));
            captured = options;
        });

        var built = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        Assert.IsType<MaxDistinctValuesPredicate>(built.Contracts[0].Predicates.Single());
        Assert.Equal(fluent.ConfigurationFingerprint, parsed.ConfigurationFingerprint);
        Assert.Equal(fluent.ConfigurationFingerprint, built.ConfigurationFingerprint);

        using var scope = AgentRunScope.Begin(null, "distinct-json", null);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(parsed, "one")).Action);
        Assert.Equal(ToolGateAction.Allow, (await Inspect(parsed, "two")).Action);
        Assert.Equal(ToolGateAction.Block, (await Inspect(parsed, "three")).Action);
    }

    [Theory]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value"}""", "missing_property")]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value","max":"1"}""", "property_type")]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value","max":0}""", "distinct_value_limit")]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value","max":4097}""", "distinct_value_limit")]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value","max":1.0}""", "distinct_value_limit")]
    [InlineData("""{"kind":"maxDistinctValues","argument":"value","max":1,"extra":true}""", "unknown_predicate_property")]
    public void JsonConfigurationRejectsInvalidShapesWithStableErrors(string predicate, string code)
    {
        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(Document(predicate)));

        Assert.Equal(code, exception.ErrorCode);
    }

    [Fact]
    public void EmbeddedSchemaAcceptsBoundedIntegerAndRejectsFractionAndOverflow()
    {
        var schema = LoadSchema();
        var valid = JsonNode.Parse(Document(
            """{"kind":"maxDistinctValues","argument":"value","max":4096}"""));
        var fraction = JsonNode.Parse(Document(
            """{"kind":"maxDistinctValues","argument":"value","max":1.5}"""));
        var overflow = JsonNode.Parse(Document(
            """{"kind":"maxDistinctValues","argument":"value","max":4097}"""));

        Assert.True(schema.Evaluate(valid, new EvaluationOptions()).IsValid);
        Assert.False(schema.Evaluate(fraction, new EvaluationOptions()).IsValid);
        Assert.False(schema.Evaluate(overflow, new EvaluationOptions()).IsValid);
    }

    [Fact]
    public void EnforcingConfigurationWithoutRunScopeIsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Contract("write", contract => contract.MaxDistinctValues("value", 2));
                options.EstablishRunScope = false;
            }));

        Assert.Contains("ToolUsageContractGate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RunScope", exception.Message, StringComparison.Ordinal);
    }

    private static ToolUsageContractGate Gate(string tool = "write", int max = 1)
        => new([new ToolContract(tool, [new MaxDistinctValuesPredicate("value", max)])]);

    private static async Task<ToolGateVerdict> Inspect(
        ToolUsageContractGate gate,
        object value,
        string tool = "write")
        => await InspectArguments(gate, tool, ("value", value));

    private static async Task<ToolGateVerdict> InspectArguments(
        ToolUsageContractGate gate,
        string tool,
        params (string Name, object? Value)[] arguments)
        => await gate.InspectAsync(new GatedToolCall(
            tool,
            arguments.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal),
            "agent", 0, 0, 1, false, null));

    private static async Task<ToolGateVerdict> InspectMissing(ToolUsageContractGate gate)
        => await gate.InspectAsync(new GatedToolCall(
            "write", new Dictionary<string, object?>(), "agent", 0, 0, 1, false, null));

    private static async Task<ToolGateVerdict> InspectNull(ToolUsageContractGate gate)
        => await gate.InspectAsync(new GatedToolCall(
            "write", new Dictionary<string, object?> { ["value"] = null }, "agent", 0, 0, 1, false, null));

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

    private static string Document(string predicate)
        => $$"""{"schema":"gatekeeper.contract/1","contracts":[{"tool":"write","predicates":[{{predicate}}]}]}""";

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "distinct-contract-test" });

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(ToolUsageContractGate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".gatekeeper-contract-v1.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private sealed class CyclicValue
    {
        public CyclicValue? Self { get; set; }
    }
}
