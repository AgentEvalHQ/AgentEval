// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Cli.CopilotStudio;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// P6 item A wired into <c>redteam --sut copilot-studio</c>: <c>--copilotstudio-save-config-baseline</c> /
/// <c>--copilotstudio-config-baseline</c> / <c>--fail-on-config-drift</c>. All assertions run through
/// <c>CopilotStudioRedTeamTarget.Validate</c> directly — the drift check is pure file I/O + fingerprinting,
/// no network call, so it needs no <c>sutOverride</c> seam to test (unlike a full scan).
/// </summary>
public class CopilotStudioConfigDriftCliTests
{
    [Fact]
    public void Validate_SaveConfigBaseline_WritesAFileWithTheResolvedFingerprint()
    {
        var cfg = WriteValidConfig();
        var baselinePath = TempPath("baseline");
        try
        {
            var target = new CopilotStudioRedTeamTarget();
            var opts = BaseOptions(cfg, saveConfigBaseline: new FileInfo(baselinePath));

            target.Validate(opts);

            Assert.True(File.Exists(baselinePath));
            var baseline = CopilotStudioConfigBaseline.FromJson(File.ReadAllText(baselinePath));
            Assert.Single(baseline.HashesByAgentName);
        }
        finally { TryDelete(cfg); TryDelete(new FileInfo(baselinePath)); }
    }

    [Fact]
    public void Validate_ConfigBaseline_UnchangedIdentity_DoesNotThrow()
    {
        var cfg = WriteValidConfig();
        var baselinePath = TempPath("baseline");
        try
        {
            // Capture a baseline from the SAME config first.
            new CopilotStudioRedTeamTarget().Validate(BaseOptions(cfg, saveConfigBaseline: new FileInfo(baselinePath)));

            // A second, fresh target instance re-validates the SAME config against that baseline.
            var target = new CopilotStudioRedTeamTarget();
            var opts = BaseOptions(cfg, configBaseline: new FileInfo(baselinePath));

            var ex = Record.Exception(() => target.Validate(opts));
            Assert.Null(ex);
        }
        finally { TryDelete(cfg); TryDelete(new FileInfo(baselinePath)); }
    }

    [Fact]
    public void Validate_ConfigBaseline_DriftedIdentity_FailOnConfigDriftFalse_WarnsButDoesNotThrow()
    {
        var originalCfg = WriteValidConfig(schemaName: "agent-original");
        var baselinePath = TempPath("baseline");
        try
        {
            new CopilotStudioRedTeamTarget().Validate(BaseOptions(originalCfg, saveConfigBaseline: new FileInfo(baselinePath)));

            var driftedCfg = WriteValidConfig(schemaName: "agent-DRIFTED");
            var target = new CopilotStudioRedTeamTarget();
            var opts = BaseOptions(driftedCfg, configBaseline: new FileInfo(baselinePath), failOnConfigDrift: false);

            var ex = Record.Exception(() => target.Validate(opts));
            Assert.Null(ex); // warn, never block, by default
        }
        finally { TryDelete(originalCfg); TryDelete(baselinePath); }
    }

    [Fact]
    public void Validate_ConfigBaseline_DriftedIdentity_FailOnConfigDriftTrue_Throws()
    {
        var originalCfg = WriteValidConfig(schemaName: "agent-original");
        var baselinePath = TempPath("baseline");
        try
        {
            new CopilotStudioRedTeamTarget().Validate(BaseOptions(originalCfg, saveConfigBaseline: new FileInfo(baselinePath)));

            var driftedCfg = WriteValidConfig(schemaName: "agent-DRIFTED");
            var target = new CopilotStudioRedTeamTarget();
            var opts = BaseOptions(driftedCfg, configBaseline: new FileInfo(baselinePath), failOnConfigDrift: true);

            var ex = Assert.Throws<InvalidOperationException>(() => target.Validate(opts));
            Assert.Contains("fail-on-config-drift", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(originalCfg); TryDelete(baselinePath); }
    }

    [Fact]
    public void Validate_ConfigBaseline_MissingFile_Throws()
    {
        var cfg = WriteValidConfig();
        try
        {
            var target = new CopilotStudioRedTeamTarget();
            var missing = new FileInfo(TempPath("nonexistent-baseline"));
            var opts = BaseOptions(cfg, configBaseline: missing);

            var ex = Assert.Throws<InvalidOperationException>(() => target.Validate(opts));
            Assert.Contains("baseline file not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(cfg); }
    }

    [Fact]
    public void Validate_NeitherBaselineOptionSet_DoesNotThrow_AndWritesNoFile()
    {
        var cfg = WriteValidConfig();
        try
        {
            var target = new CopilotStudioRedTeamTarget();
            var ex = Record.Exception(() => target.Validate(BaseOptions(cfg)));
            Assert.Null(ex);
        }
        finally { TryDelete(cfg); }
    }

    // ── scoping: the shared eval/bench --sut seam must NOT get the config-baseline flags ──

    [Fact]
    public void AddOptionsTo_RedTeamPublicMethod_RegistersConfigBaselineOptions()
    {
        var command = new System.CommandLine.Command("redteam-test");
        IRedTeamBuiltInTarget target = new CopilotStudioRedTeamTarget();
        target.AddOptionsTo(command);

        Assert.Contains(command.Options, o => o.Name == "--copilotstudio-save-config-baseline");
        Assert.Contains(command.Options, o => o.Name == "--copilotstudio-config-baseline");
        Assert.Contains(command.Options, o => o.Name == "--fail-on-config-drift");
    }

    [Fact]
    public void ISutTarget_AddOptionsTo_DoesNotRegisterConfigBaselineOptions()
    {
        var command = new System.CommandLine.Command("eval-test");
        AgentEval.Cli.Commands.Targets.ISutTarget target = new CopilotStudioRedTeamTarget();
        target.AddOptionsTo(command);

        Assert.DoesNotContain(command.Options, o => o.Name == "--copilotstudio-save-config-baseline");
        Assert.DoesNotContain(command.Options, o => o.Name == "--copilotstudio-config-baseline");
        Assert.DoesNotContain(command.Options, o => o.Name == "--fail-on-config-drift");
        // The common 3 flags are still there — same as before this feature.
        Assert.Contains(command.Options, o => o.Name == "--copilotstudio-config");
    }

    // ── helpers ──

    private static RedTeamOptions BaseOptions(
        FileInfo config, FileInfo? saveConfigBaseline = null, FileInfo? configBaseline = null, bool failOnConfigDrift = false) => new()
    {
        Sut = "copilot-studio",
        TargetOptions = new Dictionary<string, IRedTeamTargetOptions?>
        {
            ["copilot-studio"] = new CopilotStudioTargetOptions
            {
                ConfigFile = config,
                AckLiveSideEffects = true,
                MaxCredits = 0,
                SaveConfigBaseline = saveConfigBaseline,
                ConfigBaseline = configBaseline,
                FailOnConfigDrift = failOnConfigDrift,
            },
        },
        Parallelism = 1,
        Intensity = "moderate",
        Format = "json",
        FailOn = "vuln",
        Quiet = true,
    };

    private static FileInfo WriteValidConfig(string schemaName = "cr1a2_agent")
    {
        var path = TempPath("cfg");
        File.WriteAllText(path,
            $$"""{ "environmentId": "env-1", "schemaName": "{{schemaName}}", "tenantId": "tenant-1", "appClientId": "app-1" }""");
        return new FileInfo(path);
    }

    private static string TempPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"agenteval-cs-drift-{label}-" + Guid.NewGuid().ToString("N") + ".json");

    private static void TryDelete(FileInfo f)
    {
        try { if (f.Exists) { f.Delete(); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private static void TryDelete(string path) => TryDelete(new FileInfo(path));
}
