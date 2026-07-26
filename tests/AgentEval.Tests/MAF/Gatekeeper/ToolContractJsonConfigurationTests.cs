// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.1b — strict, bounded, atomic JSON/schema contract configuration.</summary>
public class ToolContractJsonConfigurationTests
{
    private const string ValidJson = """
        {
          "schema": "gatekeeper.contract/1",
          "contracts": [
            {
              "tool": "send",
              "predicates": [
                { "kind": "piiScan", "argument": "body" },
                {
                  "kind": "deniedKeywords",
                  "argument": "body",
                  "keywords": ["beta", "ALPHA"]
                }
              ]
            },
            {
              "tool": "run",
              "predicates": [
                {
                  "kind": "deniedKeywords",
                  "argument": "command",
                  "keywords": ["delete"]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task ValidJson_ProducesOperationalImmutableGate()
    {
        var gate = ResolveJson(ValidJson);

        Assert.Equal(2, gate.Contracts.Count);
        Assert.IsType<PiiPredicate>(gate.Contracts[0].Predicates[0]);
        Assert.IsType<DeniedKeywordsPredicate>(gate.Contracts[0].Predicates[1]);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("send", "body", "person@example.com"))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("run", "command", "DELETE all"))).Action);
    }

    [Fact]
    public void FluentAndJsonConfiguration_ProduceIdenticalFingerprints()
    {
        var fluent = new ToolUsageContractGate(
        [
            new ToolContract("SEND",
            [
                new PiiPredicate("body"),
                new DeniedKeywordsPredicate("body", [" alpha ", "BETA"]),
            ]),
            new ToolContract("run", [new DeniedKeywordsPredicate("command", ["DELETE"])]),
        ]);
        var json = ResolveJson(ValidJson);

        Assert.Equal(fluent.ConfigurationFingerprint, json.ConfigurationFingerprint);
        Assert.Equal(GateConfigFingerprint.Compute([fluent]), GateConfigFingerprint.Compute([json]));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void StrictParser_RejectsHostileOrMalformedDocument(string json, string expectedCode)
    {
        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(json));

        Assert.Equal(expectedCode, exception.ErrorCode);
        Assert.StartsWith("$", exception.JsonPath, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InvalidDocuments => new()
    {
        { "not json", "invalid_json" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[],}", "invalid_json" },
        { "{/*comment*/\"schema\":\"gatekeeper.contract/1\",\"contracts\":[]}", "invalid_json" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"schema\":\"gatekeeper.contract/1\",\"contracts\":[]}", "duplicate_property" },
        { "{\"schema\":\"gatekeeper.contract/2\",\"contracts\":[{}]}", "unsupported_schema" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[],\"extra\":true}", "unknown_root_property" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[]}", "contract_count_limit" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":{}}", "property_type" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[],\"extra\":true}]}", "unknown_contract_property" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[]}]}", "predicate_count_limit" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"unknown\",\"argument\":\"body\"}]}]}", "unknown_predicate_kind" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"piiScan\",\"argument\":7}]}]}", "property_type" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"piiScan\",\"argument\":\"body\",\"extra\":true}]}]}", "unknown_predicate_property" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[]}]}]}", "keyword_count_limit" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[7]}]}]}", "keyword_type" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[\" \"]}]}]}", "empty_keyword" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"piiScan\",\"argument\":\"body\"}]},{\"tool\":\"SEND\",\"predicates\":[{\"kind\":\"piiScan\",\"argument\":\"body\"}]}]}", "duplicate_tool" },
        { "{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{\"tool\":\"send\",\"predicates\":[{\"kind\":\"piiScan\",\"argument\":\"body\"},{\"kind\":\"piiScan\",\"argument\":\"body\"}]}]}", "duplicate_predicate" },
    };

    [Fact]
    public void ParsingIsAtomic_WhenLaterContractFails()
    {
        var options = new GatekeeperOptions();
        options.Contract("existing", builder => builder.Pii("body"));
        var invalidAfterValid = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [
                { "tool": "new-tool", "predicates": [{ "kind": "piiScan", "argument": "body" }] },
                { "tool": "bad-tool", "predicates": [{ "kind": "unsupported", "argument": "body" }] }
              ]
            }
            """;

        Assert.Throws<GatekeeperContractConfigurationException>(() =>
            options.LoadContractsFromJson(invalidAfterValid));

        options.Contract("new-tool", builder => builder.Pii("body"));
    }

    [Fact]
    public void JsonCollisionWithExistingFluentContract_IsAtomicAndTyped()
    {
        var options = new GatekeeperOptions();
        options.Contract("send", builder => builder.Pii("body"));
        var duplicate = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [
                { "tool": "SEND", "predicates": [{ "kind": "piiScan", "argument": "body" }] }
              ]
            }
            """;

        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            options.LoadContractsFromJson(duplicate));

        Assert.Equal("duplicate_tool", exception.ErrorCode);
        Assert.Equal("$.contracts[0].tool", exception.JsonPath);
    }

    [Fact]
    public void GeneratedJsonContracts_StillConflictWithDirectGateBeforeWiring()
    {
        var direct = new ToolUsageContractGate(
            [new ToolContract("send", [new PiiPredicate("body")])]);
        var agent = NewAgent();

        Assert.Throws<InvalidOperationException>(() =>
            agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.LoadContractsFromJson(ValidJson);
                options.Add(direct);
            }));
    }

    [Fact]
    public void Limits_AcceptDocumentedBoundaries()
    {
        var contracts = Enumerable.Range(0, 256)
            .Select(index => $"{{\"tool\":\"tool-{index}\",\"predicates\":[{{\"kind\":\"piiScan\",\"argument\":\"body\"}}]}}" );
        new GatekeeperOptions().LoadContractsFromJson(Document(string.Join(",", contracts)));

        var predicates = Enumerable.Range(0, 64)
            .Select(index => $"{{\"kind\":\"piiScan\",\"argument\":\"arg-{index}\"}}" );
        new GatekeeperOptions().LoadContractsFromJson(
            Document($"{{\"tool\":\"send\",\"predicates\":[{string.Join(",", predicates)}]}}"));

        var keywords = Enumerable.Range(0, 256).Select(index => $"\"word-{index}\"");
        new GatekeeperOptions().LoadContractsFromJson(
            Document($"{{\"tool\":\"send\",\"predicates\":[{{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[{string.Join(",", keywords)}]}}]}}"));

        new GatekeeperOptions().LoadContractsFromJson(
            Document($"{{\"tool\":\"{new string('t', 256)}\",\"predicates\":[{{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[\"{new string('k', 4096)}\"]}}]}}"));
    }

    [Theory]
    [InlineData("contracts")]
    [InlineData("predicates")]
    [InlineData("keywords")]
    [InlineData("name")]
    [InlineData("keywordLength")]
    [InlineData("payload")]
    [InlineData("depth")]
    public void Limits_RejectFirstValueAboveBoundary(string limit)
    {
        string json = limit switch
        {
            "contracts" => Document(string.Join(",", Enumerable.Range(0, 257).Select(index =>
                $"{{\"tool\":\"tool-{index}\",\"predicates\":[{{\"kind\":\"piiScan\",\"argument\":\"body\"}}]}}"))),
            "predicates" => Document($"{{\"tool\":\"send\",\"predicates\":[{string.Join(",", Enumerable.Range(0, 65).Select(index => $"{{\"kind\":\"piiScan\",\"argument\":\"arg-{index}\"}}"))}]}}"),
            "keywords" => Document($"{{\"tool\":\"send\",\"predicates\":[{{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[{string.Join(",", Enumerable.Range(0, 257).Select(index => $"\"word-{index}\""))}]}}]}}"),
            "name" => Document($"{{\"tool\":\"{new string('t', 257)}\",\"predicates\":[{{\"kind\":\"piiScan\",\"argument\":\"body\"}}]}}"),
            "keywordLength" => Document($"{{\"tool\":\"send\",\"predicates\":[{{\"kind\":\"deniedKeywords\",\"argument\":\"body\",\"keywords\":[\"{new string('k', 4097)}\"]}}]}}"),
            "payload" => new string('x', 1024 * 1024 + 1),
            "depth" => new string('[', 33) + new string(']', 33),
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };

        Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(json));
    }

    [Fact]
    public void ErrorText_DoesNotEchoConfiguredSecretsOrUnknownPropertyNames()
    {
        const string secret = "SUPER-SECRET-CONFIG-VALUE";
        var json = $$"""
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "send",
                "predicates": [{
                  "kind": "deniedKeywords",
                  "argument": "body",
                  "keywords": ["{{secret}}"],
                  "{{secret}}": "{{secret}}"
                }]
              }]
            }
            """;

        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(json));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal("$.contracts[0].predicates[0].*", exception.JsonPath);
    }

    [Fact]
    public void EmbeddedSchema_Loads_AndAgreesWithParserOnDocumentedShapes()
    {
        var schema = LoadSchema();
        var validResult = schema.Evaluate(
            JsonNode.Parse(ValidJson),
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var invalid = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "send",
                "predicates": [{ "kind": "piiScan", "argument": "body", "extra": true }]
              }]
            }
            """;
        var invalidResult = schema.Evaluate(
            JsonNode.Parse(invalid),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validResult.IsValid);
        Assert.False(invalidResult.IsValid);
        new GatekeeperOptions().LoadContractsFromJson(ValidJson);
        Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(invalid));
    }

    [Fact]
    public void FileLoading_ReadsOnce_AcceptsBom_AndDoesNotLiveReload()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, ValidJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            GatekeeperOptions? captured = null;

            NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.LoadContractsFromFile(path);
                File.WriteAllText(path, "not json");
                captured = options;
            });

            var gate = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
            Assert.Equal(2, gate.Contracts.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileLoading_RejectsInvalidUtf8OversizeAndMissingFileWithoutLeakingPath()
    {
        var invalidUtf8Path = TemporaryPath();
        var oversizedPath = TemporaryPath();
        var missingPath = TemporaryPath();
        try
        {
            File.WriteAllBytes(invalidUtf8Path, [0xFF]);
            File.WriteAllBytes(oversizedPath, new byte[1024 * 1024 + 1]);
            File.Delete(missingPath);

            Assert.Equal(
                "invalid_utf8",
                Assert.Throws<GatekeeperContractConfigurationException>(() =>
                    new GatekeeperOptions().LoadContractsFromFile(invalidUtf8Path)).ErrorCode);
            Assert.Equal(
                "payload_too_large",
                Assert.Throws<GatekeeperContractConfigurationException>(() =>
                    new GatekeeperOptions().LoadContractsFromFile(oversizedPath)).ErrorCode);
            var missing = Assert.Throws<GatekeeperContractConfigurationException>(() =>
                new GatekeeperOptions().LoadContractsFromFile(missingPath));
            Assert.Equal("file_io", missing.ErrorCode);
            Assert.DoesNotContain(missingPath, missing.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(invalidUtf8Path);
            File.Delete(oversizedPath);
            File.Delete(missingPath);
        }
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

    private static GatedToolCall Call(string tool, string argument, object value)
        => new(
            tool,
            new Dictionary<string, object?> { [argument] = value },
            "agent", 0, 0, 1, false, null);

    private static ChatClientAgent NewAgent()
        => new(
            new ScriptedChatClient().AddText("done"),
            new ChatClientAgentOptions { Name = "json-contract-test" });

    private static string Document(string contracts)
        => $"{{\"schema\":\"gatekeeper.contract/1\",\"contracts\":[{contracts}]}}";

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(ToolUsageContractGate).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(".gatekeeper-contract-v1.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string TemporaryPath()
        => Path.Combine(Path.GetTempPath(), "agenteval-contract-" + Guid.NewGuid().ToString("N") + ".json");
}
