// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.Gatekeeper;
using AgentEval.MAF.Gatekeeper.Memory;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class MemoryProtectionConfigurationTests
{
    [Fact]
    public void ParseJson_ValidStrictConfiguration_BindsReviewedPolicy()
    {
        var pipeline = Pipeline();
        var configuration = MemoryProtectionConfiguration.ParseJson("""
            {
              "schema": "gatekeeper.memory-protection/1",
              "expectedPolicyFingerprint": "POLICY",
              "minimumCoverage": "Boundary",
              "sensitiveSinkTools": ["send_email"]
            }
            """.Replace("POLICY", pipeline.PolicyFingerprint, StringComparison.Ordinal));

        Assert.Equal(pipeline.PolicyFingerprint, configuration.ExpectedPolicyFingerprint);
        Assert.Equal(MemoryCoverageLevel.Boundary, configuration.MinimumCoverage);
        Assert.Equal("send_email", Assert.Single(configuration.SensitiveSinkTools));
    }

    [Theory]
    [InlineData("{\"schema\":\"gatekeeper.memory-protection/1\",\"schema\":\"gatekeeper.memory-protection/1\",\"expectedPolicyFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"minimumCoverage\":\"Boundary\"}", "duplicate_property")]
    [InlineData("{\"schema\":\"gatekeeper.memory-protection/1\",\"expectedPolicyFingerprint\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"minimumCoverage\":\"Boundary\",\"typeName\":\"Unsafe.Runtime.Type\"}", "unknown_property")]
    public void ParseJson_DuplicateOrUnknownProperty_Refuses(string json, string reason)
    {
        var exception = Assert.Throws<MemoryProtectionConfigurationException>(() =>
            MemoryProtectionConfiguration.ParseJson(json));

        Assert.Equal(reason, exception.ReasonCode);
    }

    [Fact]
    public void UseGatekeeper_DeploymentFingerprintMismatch_Refuses()
    {
        var configuration = MemoryProtectionConfiguration.ParseJson("""
            {
              "schema": "gatekeeper.memory-protection/1",
              "expectedPolicyFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "minimumCoverage": "Boundary"
            }
            """);
        var protection = new MemoryProtectionOptions(
            Pipeline(),
            new MemoryToolOperationRegistry([]),
            new FakeAdapter())
        {
            DeploymentConfiguration = configuration,
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            Agent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
            {
                options.KnownTools = [];
                options.ProtectMemory(protection);
            }));

        Assert.Contains("different memory policy fingerprint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAgentEvalMemoryProtection_InstanceAndFactoryResolveSingleton()
    {
        var protection = Protection();
        var direct = new ServiceCollection()
            .AddAgentEvalMemoryProtection(protection)
            .BuildServiceProvider();
        var factoryCalls = 0;
        var factory = new ServiceCollection()
            .AddAgentEvalMemoryProtection(_ =>
            {
                factoryCalls++;
                return protection;
            })
            .BuildServiceProvider();

        Assert.Same(protection, direct.GetRequiredService<MemoryProtectionOptions>());
        Assert.Same(
            factory.GetRequiredService<MemoryProtectionOptions>(),
            factory.GetRequiredService<MemoryProtectionOptions>());
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void EmbeddedSchemas_ArePresentAndStrictConfigurationValidates()
    {
        var pipeline = Pipeline();
        var json = """
            {
              "schema": "gatekeeper.memory-protection/1",
              "expectedPolicyFingerprint": "POLICY",
              "minimumCoverage": "Boundary",
              "sensitiveSinkTools": []
            }
            """.Replace("POLICY", pipeline.PolicyFingerprint, StringComparison.Ordinal);

        var result = LoadSchema("memory-protection-config-v1.schema.json")
            .Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MemoryProtectionReport_ValidatesSchemaAndCarriesNestedPolicyProvenance()
    {
        var captured = new GatekeeperOptions();
        Agent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            captured = options;
            options.KnownTools = [];
            options.ProtectMemory(Protection());
        });
        var report = captured.MemoryProtectionReport!;
        var json = report.ToJson();

        var result = LoadSchema("memory-protection-report-v1.schema.json")
            .Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid);
        Assert.Equal(report.PolicyFingerprint, report.ToolCoverage.PolicyFingerprint);
        Assert.Equal(report.PolicyFingerprint, report.McpCoverage.PolicyFingerprint);
        Assert.DoesNotContain("content", json, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonSchema LoadSchema(string fileName)
    {
        var assembly = typeof(MemoryProtectionOptions).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static MemoryProtectionOptions Protection()
        => new(Pipeline(), new MemoryToolOperationRegistry([]), new FakeAdapter());

    private static MemoryGatePipeline Pipeline()
        => new(
            [],
            policy: new MemorySecurityPolicy(
                "phase7-config",
                "1",
                MemorySecurityProfile.Enforce,
                MemoryGateAction.Reject,
                MemoryCoverageLevel.Boundary));

    private static ChatClientAgent Agent()
        => new(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "phase7-config" });

    private sealed class FakeAdapter : IMemoryToolContextAdapter
    {
        public MemoryGateContext CreateCallContext(GatedToolCall call, MemoryOperationContract operation, MemoryGateStage stage)
            => throw new NotSupportedException();
        public MemoryGateContext CreateResultContext(GatedToolResult result, MemoryOperationContract operation)
            => throw new NotSupportedException();
        public IReadOnlyDictionary<string, object?> ApplySanitizedArguments(GatedToolCall call, MemoryOperationContract operation, string sanitizedContent)
            => throw new NotSupportedException();
        public object ApplySanitizedResult(GatedToolResult result, MemoryOperationContract operation, string sanitizedContent)
            => throw new NotSupportedException();
    }
}
