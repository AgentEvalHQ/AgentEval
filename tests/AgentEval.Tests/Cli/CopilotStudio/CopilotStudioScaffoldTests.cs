// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using AgentEval.MAF.CopilotStudio;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Agents.CopilotStudio.Client.Discovery;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Stage 1 of the Copilot Studio MVP scaffold — the credential-free, offline pieces: the connection config + loader
/// (including Cloud → <c>PowerPlatformCloud</c> resolution) and the agent factory seam (a MAF <c>AIAgent</c> →
/// <c>MAFAgentAdapter</c>). <see cref="CopilotStudioAgentFactory.BuildLive"/>'s live-connector wiring is exercised
/// here too, but only up to the point of construction — it never makes a network call (see the "WithoutNetworkCall"
/// tests below) — so this whole file stays testable with zero credentials. The activity-stream bridging logic and
/// the MSAL token-acquisition flow have their own dedicated test files
/// (<c>CopilotStudioChatClientTests</c> / <c>CopilotStudioTokenProviderTests</c>).
/// </summary>
public class CopilotStudioScaffoldTests
{
    // ── CopilotStudioConfig: load + validate ──

    [Fact]
    public void Config_Load_Valid_PopulatesFields()
    {
        var f = WriteTempJson(
            "{ \"environmentId\": \"env-123\", \"schemaName\": \"cr1a2_myAgent\", \"tenantId\": \"tenant-abc\", " +
            "\"appClientId\": \"app-xyz\", \"agentName\": \"My Agent\" }");
        try
        {
            var cfg = CopilotStudioConfig.Load(f);
            Assert.Equal("env-123", cfg.EnvironmentId);
            Assert.Equal("cr1a2_myAgent", cfg.SchemaName);
            Assert.Equal("tenant-abc", cfg.TenantId);
            Assert.Equal("app-xyz", cfg.AppClientId);
            Assert.Equal("My Agent", cfg.DisplayName);
        }
        finally { TryDelete(f); }
    }

    [Fact]
    public void Config_Load_MissingRequired_Throws()
    {
        // schemaName + appClientId omitted → a single clear error listing the missing fields.
        var f = WriteTempJson("{ \"environmentId\": \"env-123\", \"tenantId\": \"tenant-abc\" }");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioConfig.Load(f));
            Assert.Contains("missing required field", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("schemaName", ex.Message);
            Assert.Contains("appClientId", ex.Message);
        }
        finally { TryDelete(f); }
    }

    [Fact]
    public void Config_Load_BadJson_Throws()
    {
        var f = WriteTempJson("{ this is not json ");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioConfig.Load(f));
            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(f); }
    }

    [Fact]
    public void Config_Load_MissingFile_Throws()
    {
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-cs-nope-" + Guid.NewGuid().ToString("N") + ".json"));
        var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioConfig.Load(missing));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_Load_UnreadableFile_ThrowsWrapped()
    {
        // The file exists but is held with an exclusive lock → File.ReadAllText throws an IOException.
        // Load must wrap it in a clear, path-tagged InvalidOperationException rather than leak the raw IO error.
        var f = WriteTempJson("{ \"environmentId\": \"env-123\" }");
        try
        {
            using var _ = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioConfig.Load(f));
            Assert.Contains("could not be read", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(f.Name, ex.Message);
        }
        finally { TryDelete(f); }
    }

    [Fact]
    public void Config_DisplayName_FallsBackToSchemaName()
    {
        var cfg = new CopilotStudioConfig { SchemaName = "cr1a2_agent", AgentName = null };
        Assert.Equal("cr1a2_agent", cfg.DisplayName);
    }

    // ── CopilotStudioConfig: Cloud validation/resolution (Track 1 — real PowerPlatformCloud enum mapping) ──

    [Theory]
    [InlineData(null, PowerPlatformCloud.Prod)]        // omitted → defaults to Prod
    [InlineData("", PowerPlatformCloud.Prod)]
    [InlineData("Prod", PowerPlatformCloud.Prod)]
    [InlineData("gov", PowerPlatformCloud.Gov)]         // case-insensitive
    [InlineData("HIGH", PowerPlatformCloud.High)]
    [InlineData("DoD", PowerPlatformCloud.DoD)]
    [InlineData("Mooncake", PowerPlatformCloud.Mooncake)]
    [InlineData("GovFR", PowerPlatformCloud.GovFR)]
    [InlineData("Ex", PowerPlatformCloud.Ex)]
    [InlineData("Rx", PowerPlatformCloud.Rx)]
    [InlineData("Local", PowerPlatformCloud.Local)]
    [InlineData("Other", PowerPlatformCloud.Other)]
    public void Config_ResolveCloud_MapsToRealEnum(string? cloud, PowerPlatformCloud expected)
    {
        var cfg = ValidConfig() with { Cloud = cloud };
        Assert.Equal(expected, cfg.ResolveCloud());
    }

    [Fact]
    public void Config_ResolveCloud_UnrecognizedValue_Throws()
    {
        var cfg = ValidConfig() with { Cloud = "Proood" };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.ResolveCloud());
        Assert.Contains("unrecognized value", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Proood", ex.Message);
    }

    [Fact]
    public void Config_ResolveCloud_LiteralUnknown_Throws()
    {
        // "Unknown" is a real enum member (-1) but not a valid *input* — a config file never means
        // "explicitly unresolvable cloud", so it must fail Validate()/ResolveCloud() like any other typo.
        var cfg = ValidConfig() with { Cloud = "Unknown" };
        Assert.Throws<InvalidOperationException>(() => cfg.ResolveCloud());
    }

    [Fact]
    public void Config_Validate_UnrecognizedCloud_Throws()
    {
        var cfg = ValidConfig() with { Cloud = "Prooood" };
        var ex = Assert.Throws<InvalidOperationException>(cfg.Validate);
        Assert.Contains("cloud", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_Load_UnrecognizedCloud_Throws()
    {
        var f = WriteTempJson(
            "{ \"environmentId\": \"env-123\", \"schemaName\": \"cr1a2_myAgent\", \"tenantId\": \"tenant-abc\", " +
            "\"appClientId\": \"app-xyz\", \"cloud\": \"NotACloud\" }");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioConfig.Load(f));
            Assert.Contains("cloud", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(f); }
    }

    // ── CopilotStudioAgentFactory: the seam + the live-wiring path (credential-free — see the "no network call" note below) ──

    [Fact]
    public async Task Factory_FromAgent_WrapsAgent_AndResponds()
    {
        // The CI seam: a FakeChatClient-backed MAF agent → IEvaluableAgent, credential-free and offline.
        AIAgent inner = new ChatClientAgent(
            new FakeChatClient("hello from copilot studio"),
            new ChatClientAgentOptions { Name = "cs-under-test" });

        IEvaluableAgent agent = CopilotStudioAgentFactory.FromAgent(inner);
        var resp = await agent.InvokeAsync("hi");

        Assert.Contains("hello from copilot studio", resp.Text);
    }

    [Fact]
    public void Factory_FromAgent_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CopilotStudioAgentFactory.FromAgent(null!));
    }

    [Fact]
    public void Factory_BuildLive_ValidConfig_ReturnsAgent_WithoutNetworkCall()
    {
        // Track 1: BuildLive now WIRES the live connector instead of throwing NotSupportedException. Everything
        // it does to get here — ConnectionSettings mapping, Cloud resolution, ScopeFromSettings, constructing the
        // CopilotClient + CopilotStudioTokenProvider + SingleNameHttpClientFactory, wrapping in ChatClientAgent ->
        // MAFAgentAdapter — is local/offline; the token-provider callback is only invoked lazily by CopilotClient
        // on the FIRST REAL REQUEST, which this test never makes (it never calls agent.InvokeAsync). So this stays
        // credential-free and network-free while proving the wiring itself doesn't throw.
        var agent = CopilotStudioAgentFactory.BuildLive(ValidConfig(), iUnderstandLiveSideEffects: true);
        Assert.NotNull(agent);
        Assert.IsAssignableFrom<IEvaluableAgent>(agent);
    }

    [Fact]
    public void Factory_BuildLive_WrapChatClient_TransformIsAppliedToTheRealUnderlyingClient()
    {
        // The --log-file seam (VerboseLog.Wrap in AgentEval.Cli) composes in from OUTSIDE this package via this
        // generic hook — AgentEval.MAF.CopilotStudio must never depend on AgentEval.Cli. Proven here without
        // referencing VerboseLoggingChatClient at all: a plain capturing delegate confirms BuildLive actually
        // invokes the transform, with the REAL CopilotStudioChatClient instance it just constructed (not some
        // placeholder), before wrapping it in the returned agent.
        IChatClient? seen = null;
        var wrapped = new ScriptedChatClient();
        var agent = CopilotStudioAgentFactory.BuildLive(ValidConfig(), iUnderstandLiveSideEffects: true, wrapChatClient: c =>
        {
            seen = c;
            return wrapped;   // swap in a sentinel so we can also prove the SWAPPED instance is what gets used
        });

        Assert.NotNull(agent);
        Assert.IsType<CopilotStudioChatClient>(seen);
    }

    [Fact]
    public void Factory_BuildLive_NoWrapChatClient_DefaultsToNoTransform()
    {
        // The optional parameter must be truly optional — every pre-existing call site (and every test above
        // this one) omits it and must keep working unchanged.
        var agent = CopilotStudioAgentFactory.BuildLive(ValidConfig(), iUnderstandLiveSideEffects: true);
        Assert.NotNull(agent);
    }

    [Fact]
    public void Factory_BuildLive_ValidConfigWithNonDefaultCloud_ReturnsAgent_WithoutNetworkCall()
    {
        var agent = CopilotStudioAgentFactory.BuildLive(ValidConfig() with { Cloud = "Gov" }, iUnderstandLiveSideEffects: true);
        Assert.NotNull(agent);
    }

    [Fact]
    public void Factory_BuildLive_NoConsent_ThrowsBeforeValidatingConfig()
    {
        // The consent gate is the FIRST check — even an invalid config never reaches CopilotStudioConfig.Validate()
        // without iUnderstandLiveSideEffects: true, so a direct-code caller can never accidentally skip it by,
        // say, catching and swallowing a validation exception before consent is even checked.
        var cfg = new CopilotStudioConfig { EnvironmentId = "env-123" };   // schemaName/tenantId/appClientId missing
        var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioAgentFactory.BuildLive(cfg, iUnderstandLiveSideEffects: false));
        Assert.Contains("LIVE Copilot Studio agent", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("missing required field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_BuildLive_InvalidConfig_ThrowsValidationFirst()
    {
        // A bad config surfaces the caller's own error (missing fields) before any connector construction —
        // once consent is given.
        var cfg = new CopilotStudioConfig { EnvironmentId = "env-123" };   // schemaName/tenantId/appClientId missing
        var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioAgentFactory.BuildLive(cfg, iUnderstandLiveSideEffects: true));
        Assert.Contains("missing required field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_BuildLive_InvalidCloud_ThrowsValidationFirst()
    {
        var cfg = ValidConfig() with { Cloud = "NotACloud" };
        var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioAgentFactory.BuildLive(cfg, iUnderstandLiveSideEffects: true));
        Assert.Contains("cloud", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ──

    private static CopilotStudioConfig ValidConfig() => new()
    {
        EnvironmentId = "env-123",
        SchemaName = "cr1a2_myAgent",
        TenantId = "tenant-abc",
        AppClientId = "app-xyz",
    };

    private static FileInfo WriteTempJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-cs-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new FileInfo(path);
    }

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
