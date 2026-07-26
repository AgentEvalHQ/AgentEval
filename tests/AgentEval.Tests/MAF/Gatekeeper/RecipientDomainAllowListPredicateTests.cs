// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.2a — bounded mailbox parsing and IDN-aware recipient allow-lists.</summary>
public class RecipientDomainAllowListPredicateTests
{
    [Fact]
    public void Constructor_NormalizesIdnDeduplicatesAndDefensivelyCopies()
    {
        var domains = new[] { " BÜCHER.example. ", "xn--bcher-kva.EXAMPLE" };
        var predicate = new RecipientDomainAllowListPredicate("to", domains);
        domains[0] = "evil.example";

        Assert.Equal(["xn--bcher-kva.example"], predicate.AllowedDomains);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)predicate.AllowedDomains).Add("evil.example"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("user@example.com")]
    [InlineData("*.example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("bad_domain.example")]
    [InlineData("example..com")]
    [InlineData("example.com..")]
    public void Constructor_RejectsNonDnsAllowListEntries(string domain)
    {
        Assert.Throws<ArgumentException>(() =>
            new RecipientDomainAllowListPredicate("to", [domain]));
    }

    [Fact]
    public void Constructor_RejectsEmptyAndOverLimitDomainCollections()
    {
        Assert.Throws<ArgumentException>(() => new RecipientDomainAllowListPredicate("to", []));
        var boundary = new RecipientDomainAllowListPredicate(
            "to",
            Enumerable.Range(0, 256).Select(index => $"d{index}.example"));
        Assert.Equal(256, boundary.AllowedDomains.Count);
        Assert.Throws<ArgumentException>(() => new RecipientDomainAllowListPredicate(
            "to",
            Enumerable.Range(0, 257).Select(index => $"d{index}.example")));
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("\"Doe, Jane\" <jane@sub.EXAMPLE.com>, bob@example.com")]
    [InlineData("Alice (Ops) <alice@example.com>")]
    public async Task StringMailboxShapes_AllowExactAndSubdomains(string recipients)
    {
        var verdict = await Gate("example.com").InspectAsync(Call(recipients));

        Assert.Equal(ToolGateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task StringCollections_AndJsonStringOrArray_AreSupported()
    {
        var gate = Gate("example.com");
        using var jsonString = JsonDocument.Parse("\"alice@example.com\"");
        using var jsonArray = JsonDocument.Parse("[\"alice@example.com\",\"bob@sub.example.com\"]");

        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call(new[] { "alice@example.com", "bob@sub.example.com" }))).Action);
        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call(jsonString.RootElement.Clone()))).Action);
        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call(jsonArray.RootElement.Clone()))).Action);
    }

    [Fact]
    public async Task IdnDomains_MatchUnicodeAndPunycodeMailboxes()
    {
        var gate = Gate("bücher.example");

        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call("reader@bücher.example"))).Action);
        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call("reader@xn--bcher-kva.example"))).Action);
    }

    [Fact]
    public async Task AnyRecipientOutsideAllowList_BlocksWholeArgument()
    {
        var verdict = await Gate("example.com").InspectAsync(
            Call("alice@example.com, mallory@evil.example"));

        Assert.Equal(
            ToolGateAction.Block,
            (await Gate("example.com").InspectAsync(Call("mallory@notexample.com"))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await Gate("example.com").InspectAsync(Call("mallory@example.com.evil"))).Action);
        Assert.Equal(ToolGateAction.Block, verdict.Action);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Friends: alice@example.com;")]
    [InlineData("alice@example.com;")]
    [InlineData("alice@[127.0.0.1]")]
    [InlineData("alice@example.com\r\nBcc: mallory@evil.example")]
    [InlineData(" ")]
    public async Task MalformedGroupOrNonMailboxInput_Blocks(string recipients)
    {
        var verdict = await Gate("example.com").InspectAsync(Call(recipients));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
    }

    [Fact]
    public async Task MissingNullEmptyAndUnsupportedShapes_Block()
    {
        var gate = Gate("example.com");
        using var emptyJson = JsonDocument.Parse("[]");

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(null))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(Array.Empty<string>()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(emptyJson.RootElement.Clone()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(new object()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(MissingCall())).Action);
    }

    [Fact]
    public async Task RuntimeRecipientAndTextLimits_AreFailClosedAtBoundaryPlusOne()
    {
        var gate = Gate("example.com");
        var accepted = Enumerable.Range(0, 256).Select(index => $"user{index}@example.com").ToArray();
        var rejected = Enumerable.Range(0, 257).Select(index => $"user{index}@example.com").ToArray();

        Assert.Equal(ToolGateAction.Allow, (await gate.InspectAsync(Call(accepted))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(rejected))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call(new string('x', 64 * 1024 + 1) + "@example.com"))).Action);
    }

    [Fact]
    public async Task ThrowingStringEnumeration_FailsClosed()
    {
        var verdict = await Gate("example.com").InspectAsync(Call(ThrowingStrings()));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
    }

    [Fact]
    public async Task BlockReason_DoesNotEchoRecipientOrConfiguredDomain()
    {
        const string recipient = "secret-person@evil-secret.example";
        const string configured = "private-tenant.example";
        var verdict = await Gate(configured).InspectAsync(Call(recipient));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("recipientDomainAllowList", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(recipient, verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(configured, verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_NormalizesEquivalentDomainsAndDistinguishesPolicy()
    {
        var first = Gate(" BÜCHER.example. ", "EXAMPLE.com");
        var equivalent = Gate("example.COM", "xn--bcher-kva.example");
        var different = Gate("other.example");

        Assert.Equal(GateCost.Bounded, first.Cost);
        Assert.Equal(GateRequirements.None, first.Requirements);
        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
    }

    [Fact]
    public async Task FluentBuilder_ProducesOperationalRecipientPredicate()
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Contract("send", builder => builder.RecipientDomains("to", "example.com"));
            captured = options;
        });

        var gate = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        Assert.IsType<RecipientDomainAllowListPredicate>(gate.Contracts[0].Predicates[0]);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("user@evil.example"))).Action);
    }

    [Fact]
    public async Task JsonAndFluentModels_Agree_AndEmbeddedSchemaAcceptsRecipientKind()
    {
        const string json = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "send",
                "predicates": [{
                  "kind": "recipientDomainAllowList",
                  "argument": "to",
                  "allowedDomains": ["BÜCHER.example.", "EXAMPLE.com"]
                }]
              }]
            }
            """;
        var fromJson = ResolveJson(json);
        var fluent = Gate("example.com", "xn--bcher-kva.example");
        var schema = LoadSchema();

        Assert.Equal(fluent.ConfigurationFingerprint, fromJson.ConfigurationFingerprint);
        Assert.True(schema.Evaluate(JsonNode.Parse(json)).IsValid);
        Assert.Equal(ToolGateAction.Allow, (await fromJson.InspectAsync(Call("user@sub.example.com"))).Action);
        Assert.Equal(ToolGateAction.Block, (await fromJson.InspectAsync(Call("user@evil.example"))).Action);
    }

    [Fact]
    public void JsonParser_RejectsInvalidDomainAtomicallyWithoutLeakingIt()
    {
        const string secretDomain = "https://private-secret.example/path";
        var json = $$"""
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "send",
                "predicates": [{
                  "kind": "recipientDomainAllowList",
                  "argument": "to",
                  "allowedDomains": ["{{secretDomain}}"]
                }]
              }]
            }
            """;
        var options = new GatekeeperOptions();
        options.Contract("existing", builder => builder.Pii("body"));

        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            options.LoadContractsFromJson(json));

        Assert.Equal("invalid_domain", exception.ErrorCode);
        Assert.DoesNotContain(secretDomain, exception.ToString(), StringComparison.Ordinal);
        options.Contract("send", builder => builder.RecipientDomains("to", "example.com"));
    }

    [Fact]
    public void JsonParser_EnforcesRecipientShapeAndLimits()
    {
        var boundaryDomains = "[" + string.Join(",", Enumerable.Range(0, 256)
            .Select(index => $"\"d{index}.example\"")) + "]";
        var options = new GatekeeperOptions();
        options.LoadContractsFromJson(RecipientDocument(boundaryDomains));

        AssertCode("[]", "domain_count_limit");
        AssertCode("[7]", "domain_type");
        AssertCode($"[\"{new string('d', 257)}\"]", "domain_length_limit");
        AssertCode("[" + string.Join(",", Enumerable.Range(0, 257).Select(index => $"\"d{index}.example\"")) + "]", "domain_count_limit");

        static void AssertCode(string domainsJson, string expected)
        {
            var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromJson(RecipientDocument(domainsJson)));
            Assert.Equal(expected, exception.ErrorCode);
        }
    }

    private static ToolUsageContractGate Gate(params string[] allowedDomains)
        => new(
            [new ToolContract("send", [new RecipientDomainAllowListPredicate("to", allowedDomains)])]);

    private static string RecipientDocument(string domainsJson)
        => "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"recipientDomainAllowList\",\"argument\":\"to\",\"allowedDomains\":" +
            domainsJson + "}]}]}";

    private static GatedToolCall Call(object? value)
        => new(
            "send",
            new Dictionary<string, object?> { ["to"] = value },
            "agent", 0, 0, 1, false, null);

    private static GatedToolCall MissingCall()
        => new("send", new Dictionary<string, object?>(), "agent", 0, 0, 1, false, null);

    private static IEnumerable<string> ThrowingStrings()
    {
        yield return "user@example.com";
        throw new InvalidOperationException("enumeration failed");
    }

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

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(ToolUsageContractGate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".gatekeeper-contract-v1.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "recipient-contract-test" });
}
