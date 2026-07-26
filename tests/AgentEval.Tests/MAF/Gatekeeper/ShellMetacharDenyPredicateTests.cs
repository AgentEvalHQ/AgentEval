// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Json.Schema;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 2, Task 2.2b — explicit shell-dialect metacharacter denial.</summary>
public class ShellMetacharDenyPredicateTests
{
    [Theory]
    [MemberData(nameof(DeniedSyntaxCorpus))]
    public async Task DialectSyntaxCorpus_Blocks(ShellDialect dialect, string payload)
    {
        var verdict = await Gate(dialect).InspectAsync(Call(payload));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
    }

    public static TheoryData<ShellDialect, string> DeniedSyntaxCorpus
    {
        get
        {
            var data = new TheoryData<ShellDialect, string>();
            AddCharacters(data, ShellDialect.PowerShell, ";&|<>$`'\"(){}@#");
            AddCharacters(data, ShellDialect.PosixSh, ";&|<>$`\\'\"(){}*?[]~#");
            AddCharacters(data, ShellDialect.Cmd, "&|<>^%!\"()");
            foreach (var dialect in Enum.GetValues<ShellDialect>())
            {
                foreach (var control in new[] { '\0', '\t', '\r', '\n', '\u001b', '\u2028', '\u2029' })
                {
                    data.Add(dialect, "safe" + control + "tail");
                }
            }

            return data;
        }
    }

    [Theory]
    [InlineData(ShellDialect.PowerShell, "Get-Item safe.txt")]
    [InlineData(ShellDialect.PowerShell, "Write-Output 100% complete")]
    [InlineData(ShellDialect.PowerShell, "Write-Output alpha^beta")]
    [InlineData(ShellDialect.PosixSh, "printf hello-world")]
    [InlineData(ShellDialect.PosixSh, "printf 100%")]
    [InlineData(ShellDialect.Cmd, "dir safe-folder")]
    [InlineData(ShellDialect.Cmd, "echo alpha;beta")]
    [InlineData(ShellDialect.Cmd, "echo user@example.com")]
    public async Task DialectSpecificPlainText_Allows(ShellDialect dialect, string payload)
    {
        Assert.Equal(ToolGateAction.Allow, (await Gate(dialect).InspectAsync(Call(payload))).Action);
    }

    [Theory]
    [MemberData(nameof(EncodedBypassCorpus))]
    public async Task EncodedOrCompatibilityEquivalentMetacharacters_Block(
        ShellDialect dialect,
        string payload)
    {
        Assert.Equal(ToolGateAction.Block, (await Gate(dialect).InspectAsync(Call(payload))).Action);
    }

    public static TheoryData<ShellDialect, string> EncodedBypassCorpus => new()
    {
        { ShellDialect.PowerShell, "Write-Output%20ok%3Bwhoami" },
        { ShellDialect.PowerShell, "Write-Output\\u003Bwhoami" },
        { ShellDialect.PowerShell, "Write-Output；whoami" },
        { ShellDialect.PosixSh, "echo&#59;whoami" },
        { ShellDialect.PosixSh, Convert.ToBase64String(Encoding.UTF8.GetBytes("echo safe;whoami")) },
        { ShellDialect.PosixSh, "echo；whoami" },
        { ShellDialect.Cmd, "dir\\u0026whoami" },
        { ShellDialect.Cmd, Convert.ToBase64String(Encoding.UTF8.GetBytes("dir & whoami")) },
        { ShellDialect.Cmd, "dir＆whoami" },
    };

    [Fact]
    public async Task JsonStringShape_IsSupported_OtherShapesFailClosed()
    {
        var gate = Gate(ShellDialect.PowerShell);
        using var safeString = JsonDocument.Parse("\"Get-Item safe.txt\"");
        using var number = JsonDocument.Parse("42");

        Assert.Equal(
            ToolGateAction.Allow,
            (await gate.InspectAsync(Call(safeString.RootElement.Clone()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(number.RootElement.Clone()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(new object()))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(null))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call(string.Empty))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("   "))).Action);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(MissingCall())).Action);
    }

    [Fact]
    public async Task OversizeAndIncompleteCanonicalization_FailClosed()
    {
        var gate = Gate(ShellDialect.PowerShell);

        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call(new string('a', 64 * 1024 + 1)))).Action);
        Assert.Equal(
            ToolGateAction.Block,
            (await gate.InspectAsync(Call("%252541"))).Action);
    }

    [Fact]
    public void Constructor_RejectsUndefinedDialect()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShellMetacharDenyPredicate("command", (ShellDialect)999));
    }

    [Fact]
    public async Task BlockReason_DoesNotEchoPayload()
    {
        const string payload = "SECRET-COMMAND;invoke-evil";
        var verdict = await Gate(ShellDialect.PowerShell).InspectAsync(Call(payload));

        Assert.Equal(ToolGateAction.Block, verdict.Action);
        Assert.Contains("shellMetacharDeny", verdict.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintAndMetadata_IncludeDialect()
    {
        var first = Gate(ShellDialect.PowerShell);
        var equivalent = Gate(ShellDialect.PowerShell);
        var different = Gate(ShellDialect.PosixSh);

        Assert.Equal(GateCost.Bounded, first.Cost);
        Assert.Equal(GateRequirements.None, first.Requirements);
        Assert.Equal(first.ConfigurationFingerprint, equivalent.ConfigurationFingerprint);
        Assert.NotEqual(first.ConfigurationFingerprint, different.ConfigurationFingerprint);
    }

    [Fact]
    public async Task FluentBuilder_ProducesOperationalShellPredicate()
    {
        GatekeeperOptions? captured = null;
        NewAgent().AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, options =>
        {
            options.Contract("run", builder => builder.ShellMetacharDeny("command", ShellDialect.PosixSh));
            captured = options;
        });

        var gate = Assert.IsType<ToolUsageContractGate>(captured!.ToolGates[0]);
        var predicate = Assert.IsType<ShellMetacharDenyPredicate>(gate.Contracts[0].Predicates[0]);
        Assert.Equal(ShellDialect.PosixSh, predicate.Dialect);
        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(Call("echo;whoami"))).Action);
    }

    [Fact]
    public async Task JsonAndFluentModels_Agree_AndSchemaAcceptsExactDialect()
    {
        const string json = """
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "run",
                "predicates": [{
                  "kind": "shellMetacharDeny",
                  "argument": "command",
                  "dialect": "PowerShell"
                }]
              }]
            }
            """;
        var fromJson = ResolveJson(json);
        var fluent = Gate(ShellDialect.PowerShell);

        Assert.Equal(fluent.ConfigurationFingerprint, fromJson.ConfigurationFingerprint);
        Assert.True(LoadSchema().Evaluate(JsonNode.Parse(json)).IsValid);
        Assert.Equal(ToolGateAction.Allow, (await fromJson.InspectAsync(Call("Get-Item safe.txt"))).Action);
        Assert.Equal(ToolGateAction.Block, (await fromJson.InspectAsync(Call("Get-Item x;whoami"))).Action);
    }

    [Fact]
    public void JsonParser_RejectsUnknownOrMisCasedDialectWithoutLeakingIt()
    {
        const string secretDialect = "PowerShell-SECRET";
        var json = $$"""
            {
              "schema": "gatekeeper.contract/1",
              "contracts": [{
                "tool": "run",
                "predicates": [{
                  "kind": "shellMetacharDeny",
                  "argument": "command",
                  "dialect": "{{secretDialect}}"
                }]
              }]
            }
            """;

        var exception = Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(json));

        Assert.Equal("invalid_shell_dialect", exception.ErrorCode);
        Assert.DoesNotContain(secretDialect, exception.ToString(), StringComparison.Ordinal);
        Assert.Throws<GatekeeperContractConfigurationException>(() =>
            new GatekeeperOptions().LoadContractsFromJson(json.Replace(secretDialect, "powershell")));
    }

    private static void AddCharacters(
        TheoryData<ShellDialect, string> data,
        ShellDialect dialect,
        string characters)
    {
        foreach (var character in characters)
        {
            data.Add(dialect, "safe" + character + "tail");
        }
    }

    private static ToolUsageContractGate Gate(ShellDialect dialect)
        => new([new ToolContract("run", [new ShellMetacharDenyPredicate("command", dialect)])]);

    private static GatedToolCall Call(object? value)
        => new(
            "run",
            new Dictionary<string, object?> { ["command"] = value },
            "agent", 0, 0, 1, false, null);

    private static GatedToolCall MissingCall()
        => new("run", new Dictionary<string, object?>(), "agent", 0, 0, 1, false, null);

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
            new ChatClientAgentOptions { Name = "shell-contract-test" });
}
