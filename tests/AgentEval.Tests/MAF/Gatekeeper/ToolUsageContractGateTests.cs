// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.1a — immutable fluent tool-usage contracts and their two initial predicates.</summary>
public class ToolUsageContractGateTests
{
    private static GatedToolCall Call(string tool, params (string Name, object? Value)[] arguments)
        => new(
            tool,
            arguments.ToDictionary(item => item.Name, item => item.Value),
            "agent",
            0,
            0,
            1,
            false,
            null);

    private static ToolUsageContractGate Gate(string tool, params ContractPredicate[] predicates)
        => new([new ToolContract(tool, predicates)]);

    [Fact]
    public void PredicateBase_UsesPrivateProtectedConstructionBoundary()
    {
        var constructor = typeof(ContractPredicate)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(item => item.GetParameters() is [{ ParameterType: var type }] && type == typeof(string));

        Assert.True(constructor.IsFamilyAndAssembly);
        Assert.DoesNotContain(
            typeof(ContractPredicate).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            item => item.IsFamily);
    }

    [Fact]
    public void Models_DefensivelyCopyAllInputCollections()
    {
        var words = new[] { "blocked" };
        var predicate = new DeniedKeywordsPredicate("body", words);
        words[0] = "changed";

        ContractPredicate[] predicates = [predicate];
        var contract = new ToolContract("send", predicates);
        predicates[0] = new PiiPredicate("body");

        ToolContract[] contracts = [contract];
        var gate = new ToolUsageContractGate(contracts);
        contracts[0] = new ToolContract("other", [new PiiPredicate("body")]);

        Assert.Equal("blocked", predicate.Keywords.Single());
        Assert.Same(predicate, contract.Predicates.Single());
        Assert.Same(contract, gate.Contracts.Single());
    }

    [Fact]
    public void Constructors_RejectEmptyAndDuplicateModels()
    {
        Assert.Throws<ArgumentException>(() => new ToolContract("send", []));
        Assert.Throws<ArgumentException>(() => new ToolContract(
            "send",
            [new PiiPredicate("body"), new PiiPredicate("body")]));
        Assert.Throws<ArgumentException>(() => new ToolUsageContractGate([]));
        Assert.Throws<ArgumentException>(() => new ToolUsageContractGate(
            [
                new ToolContract("send", [new PiiPredicate("body")]),
                new ToolContract("SEND", [new PiiPredicate("body")]),
            ]));
    }

    [Fact]
    public void DeniedKeywords_NormalizeTrimDeduplicateAndRejectEmpty()
    {
        var predicate = new DeniedKeywordsPredicate("body", ["  DELETE  ", "delete", "ＤＡＮＧＥＲ"]);

        Assert.Equal(2, predicate.Keywords.Count);
        Assert.Contains("DELETE", predicate.Keywords);
        Assert.Contains("DANGER", predicate.Keywords);
        Assert.Throws<ArgumentException>(() => new DeniedKeywordsPredicate("body", [" "]));
        Assert.Throws<ArgumentException>(() => new DeniedKeywordsPredicate("body", []));
    }

    [Fact]
    public void FluentConfiguration_IsAtomicWhenCallbackOrValidationThrows()
    {
        var callbackFailure = new GatekeeperOptions();
        Assert.Throws<InvalidOperationException>(() => callbackFailure.Contract("send", builder =>
        {
            builder.Pii("body");
            throw new InvalidOperationException("stop");
        }));
        callbackFailure.Contract("SEND", builder => builder.Pii("body"));

        var validationFailure = new GatekeeperOptions();
        Assert.Throws<ArgumentException>(() =>
            validationFailure.Contract("send", builder => builder.DeniedKeywords("body", " ")));
        validationFailure.Contract("SEND", builder => builder.Pii("body"));
    }

    [Fact]
    public void FluentConfiguration_RejectsDuplicateToolWithoutChangingExistingContract()
    {
        var options = new GatekeeperOptions();
        options.Contract("send", builder => builder.Pii("body"));

        Assert.Throws<ArgumentException>(() =>
            options.Contract("SEND", builder => builder.DeniedKeywords("body", "blocked")));

        GatekeeperOptions? captured = null;
        NewAgent("send", new Dictionary<string, object?> { ["body"] = "clean" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, configured =>
            {
                configured.Contract("send", builder => builder.Pii("body"));
                captured = configured;
            });

        var generated = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates.Single());
        Assert.Single(generated.Contracts);
        Assert.IsType<PiiPredicate>(generated.Contracts[0].Predicates.Single());
    }

    [Fact]
    public async Task ToolNamesIgnoreCase_ArgumentNamesRemainExactOrdinal()
    {
        var gate = Gate("Send_Email", new PiiPredicate("Body"));

        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call("SEND_EMAIL", ("Body", "clean")))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(new GatedToolCall(
                "send_email",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["body"] = "clean" },
                "agent", 0, 0, 1, false, null))).Action);
    }

    [Fact]
    public async Task UncontractedTool_Allows()
    {
        var gate = Gate("send", new PiiPredicate("body"));
        var verdict = await gate.InspectAsync(Call("other", ("body", "person@example.com")));
        Assert.Equal(ToolGateAction.Allow, verdict.Action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task PiiPredicate_MissingOrNullArgument_Blocks(object? value)
    {
        var gate = Gate("send", new PiiPredicate("body"));
        var call = value is null
            ? Call("send", ("body", null))
            : new GatedToolCall("send", new Dictionary<string, object?>(), "agent", 0, 0, 1, false, null);

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task UnserializableOrOversizedArgument_FailsClosed()
    {
        var gate = Gate("send", new PiiPredicate("body"));

        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("send", ("body", new CyclicValue())))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("send", ("body", new ThrowingValue())))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call(
                "send",
                ("body", new string('x', ArgumentCanonicalizer.DefaultMaxLength + 1))))).Action);
    }

    [Fact]
    public async Task CanonicalizationDepthLimit_IsInconclusiveAndBlocks()
    {
        var gate = Gate("send", new PiiPredicate("body"));

        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("send", ("body", "%252541")))).Action);
    }

    [Fact]
    public async Task PiiPredicate_BlocksRawAndEncodedPii_AllowsCleanText()
    {
        var gate = Gate("send", new PiiPredicate("body"));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("person@example.com"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send", ("body", "person@example.com")))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send", ("body", encoded)))).Action);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("send", ("body", "hello team")))).Action);
    }

    [Fact]
    public async Task PiiPredicate_ScansJsonProjectionForNonStringValues()
    {
        var gate = Gate("send", new PiiPredicate("payload"));
        var payload = new Dictionary<string, object?> { ["contact"] = "person@example.com" };

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("send", ("payload", payload)))).Action);
    }

    [Fact]
    public async Task DeniedKeywords_AreLiteralCaseInsensitiveUnicodeNormalizedAndDecodeAware()
    {
        var gate = Gate(
            "run",
            new DeniedKeywordsPredicate("command", ["delete-everything", ".", "DANGER"]));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("delete-everything"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("run", ("command", "DELETE-EVERYTHING")))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("run", ("command", encoded)))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("run", ("command", "ＤＡＮＧＥＲ")))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("run", ("command", "a.b")))).Action);
        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call("run", ("command", "plain text")))).Action);
    }

    [Fact]
    public async Task FirstViolationBlocks_AndReasonNeverEchoesValueOrConfiguredKeyword()
    {
        var gate = Gate(
            "send",
            new DeniedKeywordsPredicate("body", ["TOP-SECRET-KEYWORD"]),
            new PiiPredicate("body"));

        var verdict = await gate.InspectAsync(Call("send", ("body", "TOP-SECRET-KEYWORD person@example.com")));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("send", verdict.Reason!, StringComparison.Ordinal);
        Assert.Contains("body", verdict.Reason!, StringComparison.Ordinal);
        Assert.Contains("deniedKeywords", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-KEYWORD", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchingContract_ObservesCancellationBetweenPredicates()
    {
        var gate = Gate("send", new PiiPredicate("body"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await gate.InspectAsync(Call("send", ("body", "clean")), cancellation.Token));
    }

    [Fact]
    public void CostRequirementsAndFingerprint_AggregateAndDistinguishConfiguration()
    {
        var first = Gate("send", new DeniedKeywordsPredicate("body", ["beta", "alpha"]));
        var equivalent = Gate("SEND", new DeniedKeywordsPredicate("body", [" alpha ", "BETA"]));
        var different = Gate("send", new DeniedKeywordsPredicate("body", ["gamma"]));

        Assert.Equal(GateCost.Bounded, first.Cost);
        Assert.Equal(GateRequirements.None, first.Requirements);
        Assert.Equal(first.PolicyName, different.PolicyName);
        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
        Assert.NotEqual(
            GateConfigFingerprint.Compute([first]),
            GateConfigFingerprint.Compute([different]));
    }

    [Fact]
    public void UseGatekeeper_GeneratedGateIsInsertedFirstBeforeCoverageAndWiring()
    {
        var tool = AIFunctionFactory.Create((string body) => body, "send");
        GatekeeperOptions? captured = null;

        NewAgent(tool, "send", new Dictionary<string, object?> { ["body"] = "clean" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Add(new ForbiddenToolGate("never"));
                options.Contract("send", builder => builder.Pii("body"));
                options.KnownTools = [tool];
                captured = options;
            });

        Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        Assert.IsType<ForbiddenToolGate>(captured.ToolGates[1]);
        Assert.Equal(
            ["ToolUsageContractGate", "ForbiddenToolGate"],
            captured.CoverageReport!.RegisteredToolGateNames);
    }

    [Fact]
    public void UseGatekeeper_DirectGateIsNormalizedToSameFirstSlot()
    {
        var direct = Gate("send", new PiiPredicate("body"));
        GatekeeperOptions? captured = null;

        NewAgent("send", new Dictionary<string, object?> { ["body"] = "clean" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Add(new ForbiddenToolGate("never"));
                options.Add(direct);
                captured = options;
            });

        Assert.Same(direct, captured!.ToolGates[0]);
        Assert.IsType<ForbiddenToolGate>(captured.ToolGates[1]);
    }

    [Fact]
    public void UseGatekeeper_RejectsGeneratedPlusDirectOrMultipleDirectGates()
    {
        var direct = Gate("send", new PiiPredicate("body"));
        var agent = NewAgent("send", new Dictionary<string, object?> { ["body"] = "clean" });

        Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Contract("send", builder => builder.Pii("body"));
                options.Add(direct);
            }));

        Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Add(direct);
                options.Add(Gate("other", new PiiPredicate("body")));
            }));
    }

    [Fact]
    public async Task UseGatekeeper_GeneratedGateActuallyBlocksToolAndRecordsSecretFreeEvidence()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create((string body) =>
        {
            Interlocked.Increment(ref executed);
            return body;
        }, "send");
        var trace = new AgentTrace();
        var gated = NewAgent(tool, "send", new Dictionary<string, object?> { ["body"] = "person@example.com" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.Contract("send", builder => builder.Pii("body"));
                options.Trace = trace;
            })
            .Build();

        await gated.RunAsync("go");

        Assert.Equal(0, executed);
        var key = trace.Metadata!.Keys.Single(item => item.Contains("ToolUsageContractGate", StringComparison.Ordinal));
        var evidence = (IDictionary<string, object?>)trace.Metadata[key];
        var reason = Assert.IsType<string>(evidence["reason"]);
        Assert.DoesNotContain("person@example.com", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPiiScanner_ReportsCategoriesAndRedactionWithoutMatchedValues()
    {
        var clean = PiiScanner.Scan("hello team");
        var match = PiiScanner.Scan("contact person@example.com");

        Assert.Equal(PiiScanStatus.Clean, clean.Status);
        Assert.Equal(PiiScanStatus.Match, match.Status);
        Assert.Equal(["Email"], match.DetectedKinds);
        Assert.DoesNotContain("person@example.com", match.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.com", string.Join(",", match.DetectedKinds), StringComparison.Ordinal);
    }

    private static ChatClientAgent NewAgent(string toolName, IDictionary<string, object?> arguments)
    {
        var tool = AIFunctionFactory.Create((string body) => body, toolName);
        return NewAgent(tool, toolName, arguments);
    }

    private static ChatClientAgent NewAgent(
        AIFunction tool,
        string toolName,
        IDictionary<string, object?> arguments)
    {
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", toolName, arguments)
            .AddText("done");
        return new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "contract-test",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
    }

    private sealed class CyclicValue
    {
        public CyclicValue Self => this;
    }

    private sealed class ThrowingValue
    {
        public string Value => throw new InvalidOperationException("cannot project");
    }
}
