// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Stage 1 of the Copilot Studio MVP scaffold — the credential-free, offline pieces: the connection config + loader
/// and the agent factory seam (a MAF <c>AIAgent</c> → <c>MAFAgentAdapter</c>). The live connector is deferred; these
/// exercise everything that does NOT need a live MCS agent, so the whole path is testable with zero credentials.
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

    // ── CopilotStudioAgentFactory: the seam + the deferred live path ──

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
    public void Factory_BuildLive_Deferred_ThrowsClearError()
    {
        var cfg = new CopilotStudioConfig
        {
            EnvironmentId = "env-123",
            SchemaName = "cr1a2_myAgent",
            TenantId = "tenant-abc",
            AppClientId = "app-xyz",
        };
        var ex = Assert.Throws<NotSupportedException>(() => CopilotStudioAgentFactory.BuildLive(cfg));
        Assert.Contains("not wired yet", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_BuildLive_InvalidConfig_ThrowsValidationFirst()
    {
        // A bad config surfaces the caller's own error (missing fields), not the "not wired" deferral.
        var cfg = new CopilotStudioConfig { EnvironmentId = "env-123" };   // schemaName/tenantId/appClientId missing
        var ex = Assert.Throws<InvalidOperationException>(() => CopilotStudioAgentFactory.BuildLive(cfg));
        Assert.Contains("missing required field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── helpers ──

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
