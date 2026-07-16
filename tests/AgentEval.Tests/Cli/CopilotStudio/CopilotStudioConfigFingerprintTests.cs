// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using AgentEval.Guardrails;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// P6 item A (<c>strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md</c> §1A):
/// <c>CopilotStudioConfigFingerprint</c>/<c>CopilotStudioConfigBaseline</c> — pure composition over the
/// already-tested <see cref="ManifestFingerprint"/>/<see cref="ManifestDriftDetector"/> primitive (Skills
/// Phase 4b), applied to a Copilot Studio config instead of a skill manifest. A composition test, not
/// re-testing the primitive.
/// </summary>
public class CopilotStudioConfigFingerprintTests
{
    private static CopilotStudioConfig Config(string env = "env-1", string schema = "agent-1", string? cloud = null) =>
        new() { EnvironmentId = env, SchemaName = schema, TenantId = "tenant-1", AppClientId = "app-1", Cloud = cloud };

    [Fact]
    public void CanonicalContent_SameIdentity_ProducesSameContent()
    {
        var a = Config();
        var b = Config();
        Assert.Equal(CopilotStudioConfigFingerprint.CanonicalContent(a), CopilotStudioConfigFingerprint.CanonicalContent(b));
    }

    [Fact]
    public void CanonicalContent_DifferentSchemaName_ProducesDifferentContent()
    {
        var a = Config(schema: "agent-1");
        var b = Config(schema: "agent-2");
        Assert.NotEqual(CopilotStudioConfigFingerprint.CanonicalContent(a), CopilotStudioConfigFingerprint.CanonicalContent(b));
    }

    [Fact]
    public void CanonicalContent_DifferentEnvironmentId_ProducesDifferentContent()
    {
        var a = Config(env: "env-1");
        var b = Config(env: "env-2");
        Assert.NotEqual(CopilotStudioConfigFingerprint.CanonicalContent(a), CopilotStudioConfigFingerprint.CanonicalContent(b));
    }

    [Fact]
    public void CanonicalContent_NullCloudVsExplicitProd_ProduceSameContent()
    {
        // Cloud is RESOLVED (not the raw nullable field) so an omitted cloud and an explicit "Prod" don't
        // spuriously fingerprint as different agents.
        var omitted = Config(cloud: null);
        var explicitProd = Config(cloud: "Prod");
        Assert.Equal(CopilotStudioConfigFingerprint.CanonicalContent(omitted), CopilotStudioConfigFingerprint.CanonicalContent(explicitProd));
    }

    [Fact]
    public void CanonicalContent_DifferentCloud_ProducesDifferentContent()
    {
        var prod = Config(cloud: "Prod");
        var gov = Config(cloud: "Gov");
        Assert.NotEqual(CopilotStudioConfigFingerprint.CanonicalContent(prod), CopilotStudioConfigFingerprint.CanonicalContent(gov));
    }

    [Fact]
    public void CanonicalContent_DifferentTenantOrAppClientId_ProducesSameContent()
    {
        // Deliberately excluded — TenantId/AppClientId are how you AUTHENTICATE, not which agent you're
        // talking to (a different Entra app registration can legitimately connect to the same agent).
        var a = new CopilotStudioConfig { EnvironmentId = "env-1", SchemaName = "agent-1", TenantId = "tenant-A", AppClientId = "app-A" };
        var b = new CopilotStudioConfig { EnvironmentId = "env-1", SchemaName = "agent-1", TenantId = "tenant-B", AppClientId = "app-B" };
        Assert.Equal(CopilotStudioConfigFingerprint.CanonicalContent(a), CopilotStudioConfigFingerprint.CanonicalContent(b));
    }

    [Fact]
    public void Fingerprint_IsDeterministic()
    {
        var config = Config();
        Assert.Equal(CopilotStudioConfigFingerprint.Fingerprint(config), CopilotStudioConfigFingerprint.Fingerprint(config));
    }

    [Fact]
    public void CheckDrift_UnchangedConfig_ReportsNoChangedFinding()
    {
        var config = Config();
        var baseline = CopilotStudioConfigBaseline.Capture(config);

        var drift = CopilotStudioConfigFingerprint.CheckDrift(config, baseline);

        Assert.DoesNotContain(drift, f => f.Kind == ManifestDriftKind.Changed);
        Assert.Contains(drift, f => f.Kind == ManifestDriftKind.Unchanged);
    }

    [Fact]
    public void CheckDrift_ChangedSchemaName_ReportsNewAndRemoved()
    {
        // Different DisplayName -> different dictionary key -> New/Removed rather than Changed (matches
        // ManifestDriftDetector's own key-based semantics — a renamed agent looks like "old gone, new arrived").
        var original = Config(schema: "agent-1");
        var baseline = CopilotStudioConfigBaseline.Capture(original);

        var renamed = Config(schema: "agent-2");
        var drift = CopilotStudioConfigFingerprint.CheckDrift(renamed, baseline);

        Assert.Contains(drift, f => f.Kind == ManifestDriftKind.New && f.Key == "agent-2");
        Assert.Contains(drift, f => f.Kind == ManifestDriftKind.Removed && f.Key == "agent-1");
    }

    [Fact]
    public void CheckDrift_SameNameDifferentEnvironment_ReportsChanged()
    {
        // Same DisplayName (AgentName override keeps the key stable) but a different EnvironmentId — a real
        // rug-pull shape: same reported label, different underlying agent.
        var original = new CopilotStudioConfig { EnvironmentId = "env-1", SchemaName = "agent-1", TenantId = "t", AppClientId = "a", AgentName = "MyAgent" };
        var baseline = CopilotStudioConfigBaseline.Capture(original);

        var drifted = new CopilotStudioConfig { EnvironmentId = "env-2", SchemaName = "agent-1", TenantId = "t", AppClientId = "a", AgentName = "MyAgent" };
        var drift = CopilotStudioConfigFingerprint.CheckDrift(drifted, baseline);

        Assert.Contains(drift, f => f.Kind == ManifestDriftKind.Changed && f.Key == "MyAgent");
    }

    // ── CopilotStudioConfigBaseline: JSON round-trip + file persistence ──

    [Fact]
    public void Baseline_ToJson_FromJson_RoundTrips()
    {
        var config = Config();
        var baseline = CopilotStudioConfigBaseline.Capture(config, notes: "test note");

        var roundTripped = CopilotStudioConfigBaseline.FromJson(baseline.ToJson());

        Assert.Equal(baseline.HashesByAgentName, roundTripped.HashesByAgentName);
        Assert.Equal(baseline.Notes, roundTripped.Notes);
    }

    [Fact]
    public async Task Baseline_SaveAsync_LoadAsync_RoundTrips()
    {
        var config = Config();
        var baseline = CopilotStudioConfigBaseline.Capture(config, notes: "file round-trip");
        var path = Path.Combine(Path.GetTempPath(), "agenteval-cs-baseline-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await baseline.SaveAsync(path);
            var loaded = await CopilotStudioConfigBaseline.LoadAsync(path);

            Assert.Equal(baseline.HashesByAgentName, loaded.HashesByAgentName);
            Assert.Equal(baseline.Notes, loaded.Notes);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Capture_KeysBaselineByDisplayName()
    {
        var config = new CopilotStudioConfig { EnvironmentId = "env-1", SchemaName = "schema-1", TenantId = "t", AppClientId = "a", AgentName = "Friendly Name" };
        var baseline = CopilotStudioConfigBaseline.Capture(config);

        Assert.True(baseline.HashesByAgentName.ContainsKey("Friendly Name"));
    }
}
