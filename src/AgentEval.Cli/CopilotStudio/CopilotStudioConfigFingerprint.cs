// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// P6 item A (<c>strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md</c> §1A):
/// deterministic hash-pin-and-diff drift detection over a Copilot Studio config's AGENT IDENTITY — catches
/// pointing a baseline comparison at a config that silently now targets a different agent (a rug-pull, or
/// simply a mistake — comparing today's attack results against a stale baseline from a DIFFERENT agent is
/// meaningless). No calibration debt (pure hashing, not a judge); zero new machinery beyond
/// <see cref="ManifestFingerprint"/>/<see cref="ManifestDriftDetector"/> — the exact same primitive Skills
/// Phase 4b already proved out for <c>AgentEval.Skills.SkillManifestPoisoningGate</c>/<c>SkillManifestBaseline</c>
/// (the literal template this file mirrors), applied to a third artifact type.
/// </summary>
internal static class CopilotStudioConfigFingerprint
{
    // Same field-separator convention as SkillManifestPoisoningGate — a char that cannot appear in any field.
    private const char FieldSeparator = '␞';

    /// <summary>
    /// Renders the parts of a <see cref="CopilotStudioConfig"/> that define AGENT IDENTITY —
    /// <c>EnvironmentId</c>, <c>SchemaName</c>, and the resolved <c>Cloud</c> — into one canonical string.
    /// Deliberately excludes <c>TenantId</c>/<c>AppClientId</c> (how you AUTHENTICATE, not which agent you're
    /// talking to — a different Entra app registration can legitimately connect to the SAME agent) and
    /// <c>AgentName</c> (a free-text display label, not identity). <see cref="CopilotStudioConfig"/> carries
    /// no secret/token field at all, so there is nothing to exclude on that front (unlike the general
    /// pattern's "excluding secrets" caveat). <c>Cloud</c> is resolved (not the raw nullable field) so an
    /// omitted <c>cloud</c> and an explicit <c>"cloud": "Prod"</c> fingerprint identically.
    /// </summary>
    public static string CanonicalContent(CopilotStudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return string.Join(FieldSeparator, config.EnvironmentId, config.SchemaName, config.ResolveCloud().ToString());
    }

    /// <summary>SHA-256 fingerprint of <paramref name="config"/>'s canonical identity content.</summary>
    public static string Fingerprint(CopilotStudioConfig config) => ManifestFingerprint.Hash(CanonicalContent(config));

    /// <summary>
    /// Checks the current config against a pinned <paramref name="baseline"/> and reports drift — the
    /// config's fingerprint compared against its trust-time pin (keyed by <see cref="CopilotStudioConfig.DisplayName"/>).
    /// </summary>
    public static IReadOnlyList<ManifestDriftFinding> CheckDrift(CopilotStudioConfig currentConfig, CopilotStudioConfigBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(currentConfig);
        ArgumentNullException.ThrowIfNull(baseline);

        var current = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [currentConfig.DisplayName] = Fingerprint(currentConfig),
        };
        return ManifestDriftDetector.Detect(baseline.HashesByAgentName, current);
    }
}

/// <summary>
/// A trust-time pin: the fingerprint of a Copilot Studio agent's identity at the moment its config was first
/// resolved/approved for baseline comparisons. Persisted as JSON, mirroring
/// <c>AgentEval.Skills.SkillManifestBaseline</c>'s exact shape (itself mirroring the repo's existing RedTeam
/// baseline/diff CI convention — <c>AgentEval.RedTeam.Baseline.RedTeamBaseline</c>: capture → save → later
/// load → compare → warn/fail CI on drift) — a dictionary keyed by agent display name rather than a single
/// hash, for forward-compatibility with a future multi-config baseline file, even though a
/// <c>redteam --sut copilot-studio</c> run only ever targets one agent per invocation today.
/// </summary>
/// <param name="CapturedAt">When this baseline was captured.</param>
/// <param name="HashesByAgentName"><see cref="CopilotStudioConfig.DisplayName"/> → <see cref="CopilotStudioConfigFingerprint.Fingerprint"/> at capture time.</param>
/// <param name="Notes">Optional human note (who approved it, why).</param>
internal sealed record CopilotStudioConfigBaseline(
    DateTimeOffset CapturedAt, IReadOnlyDictionary<string, string> HashesByAgentName, string? Notes = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Captures a baseline from the current config (fingerprints it now).</summary>
    public static CopilotStudioConfigBaseline Capture(CopilotStudioConfig config, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [config.DisplayName] = CopilotStudioConfigFingerprint.Fingerprint(config),
        };
        return new CopilotStudioConfigBaseline(DateTimeOffset.UtcNow, hashes, notes);
    }

    /// <summary>Serializes this baseline to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes a baseline from JSON (as produced by <see cref="ToJson"/>).</summary>
    public static CopilotStudioConfigBaseline FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<CopilotStudioConfigBaseline>(json)
            ?? throw new InvalidOperationException("Copilot Studio config baseline JSON deserialized to null.");
    }

    /// <summary>Saves this baseline to a file (CI recipe: commit this file; a later run/CI job loads and compares against it).</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        => await File.WriteAllTextAsync(path, ToJson(), cancellationToken).ConfigureAwait(false);

    /// <summary>Loads a baseline from a file.</summary>
    public static async Task<CopilotStudioConfigBaseline> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return FromJson(json);
    }
}
