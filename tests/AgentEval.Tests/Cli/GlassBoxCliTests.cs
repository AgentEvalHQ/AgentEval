// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Tracing;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Glass Box Phase 8 (T8.1/T8.2): the CLI <c>doctor</c> double-wrapping check and the <c>migrate</c>
/// v1.0→v1.1 trace-schema bump. Both helpers scan a directory for <c>*.trace.json</c>, so they are tested
/// directly against a temp directory.
/// </summary>
public class GlassBoxCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glassbox-cli-" + Guid.NewGuid().ToString("N"));

    public GlassBoxCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> WriteTraceAsync(string name, AgentTrace trace)
    {
        var path = Path.Combine(_dir, name);
        await TraceSerializer.SaveToFileAsync(trace, path);
        return path;
    }

    [Fact]
    public async Task Doctor_DoubleWrappingCheck_WarnsOnMixedScopeTrace()
    {
        // Arrange — a trace with BOTH agent-boundary (v1.0-shaped) and chat-boundary entries.
        var mixed = new AgentTrace();
        mixed.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });            // agent boundary
        mixed.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, Text = "ok" });             // agent boundary
        mixed.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "ok", 5, null, null, "stop", null));               // chat boundary
        await WriteTraceAsync("mixed.trace.json", mixed);

        // Act
        var warnings = await DoctorCommand.CheckDoubleWrappingAsync(_dir);

        // Assert
        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task Doctor_DoubleWrappingCheck_DoesNotWarnOnSingleLayerTraces()
    {
        // Arrange — a pure chat-boundary trace and a pure agent-boundary trace (neither is double-wrapped).
        var chatOnly = new AgentTrace();
        chatOnly.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "ok", 5, null, null, "stop", null));
        await WriteTraceAsync("chat.trace.json", chatOnly);

        var agentOnly = new AgentTrace { Version = "1.0" };
        agentOnly.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });
        agentOnly.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, Text = "ok" });
        await WriteTraceAsync("agent.trace.json", agentOnly);

        // Act
        var warnings = await DoctorCommand.CheckDoubleWrappingAsync(_dir);

        // Assert
        Assert.Equal(0, warnings);
    }

    // ── Gatekeeper M2d: double-gating check + StageFromKey ──

    [Fact]
    public void GateMetadataReader_StageFromKey_ExtractsStage()
    {
        Assert.Equal("tool", GateMetadataReader.StageFromKey("gate.tool.1.ForbiddenToolGate"));
        Assert.Equal("run-pre", GateMetadataReader.StageFromKey("gate.run-pre.2.KeywordGate"));
        Assert.Equal("pre", GateMetadataReader.StageFromKey("gate.pre.1.pii"));
        Assert.Equal("tool", GateMetadataReader.StageFromKey("gate.tool.1.My.Dotted.Policy"));   // policy may contain dots

        // Malformed / non-gate keys => null (not misclassified), consistent with the XML doc.
        Assert.Null(GateMetadataReader.StageFromKey("gate.tool"));                 // too few segments
        Assert.Null(GateMetadataReader.StageFromKey("notgate.pre.1.Policy"));      // not a gate key
        Assert.Null(GateMetadataReader.StageFromKey("gate..1.Policy"));            // empty stage
        Assert.Null(GateMetadataReader.StageFromKey("gate.pre..Policy"));          // empty seq
        Assert.Null(GateMetadataReader.StageFromKey("gate.pre.1."));               // empty policy
        Assert.Null(GateMetadataReader.StageFromKey("gate.pre.1..Policy"));        // leading-dot policy (empty segment)
        Assert.Null(GateMetadataReader.StageFromKey("gate.pre.1.Policy."));        // trailing-dot policy
        Assert.Null(GateMetadataReader.PolicyFromKey("gate.pre.1..Policy"));       // would otherwise yield ".Policy"
        Assert.Null(GateMetadataReader.PolicyFromKey("notgate.pre.1.Policy"));     // sibling: same guard
    }

    [Fact]
    public async Task Doctor_DoubleGatingCheck_WarnsWhenPolicyGatesAtBothSeams()
    {
        // The SAME policy ("Pii") recorded under a chat seam (pre) AND an agent seam (tool) — gating twice.
        var trace = new AgentTrace();
        trace.SetMetadata("gate.pre.1.Pii", new Dictionary<string, object?> { ["action"] = "Block" });
        trace.SetMetadata("gate.tool.2.Pii", new Dictionary<string, object?> { ["action"] = "Block" });
        await WriteTraceAsync("doublegate.trace.json", trace);

        var warnings = await DoctorCommand.CheckDoubleGatingAsync(_dir);

        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task Doctor_DoubleGatingCheck_DoesNotWarn_WhenSeamsDiffer()
    {
        // Two DIFFERENT policies, one per seam — not double-gating.
        var trace = new AgentTrace();
        trace.SetMetadata("gate.run-pre.1.Injection", new Dictionary<string, object?> { ["action"] = "Block" });
        trace.SetMetadata("gate.tool.2.ForbiddenToolGate", new Dictionary<string, object?> { ["action"] = "Block" });
        await WriteTraceAsync("singlegate.trace.json", trace);

        var warnings = await DoctorCommand.CheckDoubleGatingAsync(_dir);

        Assert.Equal(0, warnings);
    }

    [Fact]
    public async Task Migrate_TraceSchemaBump_RewritesV10ToV11WhenApplied()
    {
        // Arrange
        var v10 = new AgentTrace { Version = "1.0" };
        v10.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });
        var path = await WriteTraceAsync("legacy.trace.json", v10);

        // Act
        var count = await MigrateCommand.MigrateTraceSchemaAsync(_dir, apply: true, tag: "[T]");

        // Assert — reported one bump and the file is now v1.1, still loadable
        Assert.Equal(1, count);
        var reloaded = await TraceSerializer.LoadFromFileAsync(path);
        Assert.Equal("1.1", reloaded.Version);
        Assert.Single(reloaded.Entries);
    }

    [Fact]
    public async Task Migrate_TraceSchemaBump_DryRun_DoesNotMutate()
    {
        // Arrange
        var v10 = new AgentTrace { Version = "1.0" };
        var path = await WriteTraceAsync("legacy.trace.json", v10);

        // Act — dry run (apply:false) still reports the count but must not write
        var count = await MigrateCommand.MigrateTraceSchemaAsync(_dir, apply: false, tag: "[DRY]");

        // Assert
        Assert.Equal(1, count);
        var reloaded = await TraceSerializer.LoadFromFileAsync(path);
        Assert.Equal("1.0", reloaded.Version);
    }

    [Fact]
    public async Task Migrate_TraceSchemaBump_SkipsAlreadyV11()
    {
        // Arrange — a v1.1 trace should not be counted/rewritten
        await WriteTraceAsync("modern.trace.json", new AgentTrace { Version = "1.1" });

        // Act
        var count = await MigrateCommand.MigrateTraceSchemaAsync(_dir, apply: true, tag: "[T]");

        // Assert
        Assert.Equal(0, count);
    }
}
