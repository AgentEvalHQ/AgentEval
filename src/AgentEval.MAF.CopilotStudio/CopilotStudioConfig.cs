// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.CopilotStudio.Client.Discovery;

namespace AgentEval.MAF.CopilotStudio;

/// <summary>
/// Connection settings for a Microsoft Copilot Studio (MCS) agent evaluated via
/// <c>redteam --sut copilot-studio --copilotstudio-config &lt;file.json&gt;</c>. This identifies a <b>non-prod</b>
/// published agent and the Entra app used for Direct-to-Engine (Power Platform API) auth. It carries <b>no secret</b>
/// — the token is acquired at run time (device-code → persisted MSAL cache) and never stored here.
/// </summary>
/// <remarks>
/// The live connector (<see cref="CopilotStudioAgentFactory.BuildLive"/>) constructs a real
/// <c>Microsoft.Agents.CopilotStudio.Client.CopilotClient</c> from this config; this type + loader are what make
/// the CLI surface, validation, and the (credential-free) CI path testable independent of that wiring.
/// </remarks>
public sealed record CopilotStudioConfig
{
    /// <summary>The Power Platform environment id hosting the agent.</summary>
    public string EnvironmentId { get; init; } = "";

    /// <summary>The agent's schema name (the MCS bot identifier), e.g. <c>cr1a2_myAgent</c>.</summary>
    public string SchemaName { get; init; } = "";

    /// <summary>The Entra tenant id the agent + app registration live in.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The Entra app-registration client id used for device-code auth (never a secret — public client).</summary>
    public string AppClientId { get; init; } = "";

    /// <summary>
    /// Optional Power Platform cloud. Free text here (not the SDK's own <see cref="PowerPlatformCloud"/> enum
    /// type) so a bad/typo'd value fails <see cref="Validate"/> with this type's own clear, config-file-scoped
    /// error instead of a JSON deserialization exception naming an SDK-internal type. Matches
    /// <see cref="PowerPlatformCloud"/>'s member names exactly, case-insensitively: <c>Prod</c> (default when
    /// omitted), <c>Gov</c>, <c>High</c>, <c>DoD</c>, <c>Mooncake</c>, <c>GovFR</c>, <c>Ex</c>, <c>Rx</c>,
    /// <c>Local</c>, <c>Other</c> — plus the Microsoft-internal-only <c>Exp</c>/<c>Dev</c>/<c>Test</c>/
    /// <c>Preprod</c>/<c>FirstRelease</c>/<c>Prv</c>, which are accepted but not expected for a real tenant.
    /// </summary>
    public string? Cloud { get; init; }

    /// <summary>Optional display name for reports; falls back to <see cref="SchemaName"/>.</summary>
    public string? AgentName { get; init; }

    /// <summary>Report-facing name — <see cref="AgentName"/> if set, else <see cref="SchemaName"/>.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(AgentName) ? SchemaName : AgentName!;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads + validates the config from a JSON file, or throws a clear, actionable error.</summary>
    public static CopilotStudioConfig Load(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"Copilot Studio config file not found: {file.FullName}");
        }

        string json;
        try
        {
            json = File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Copilot Studio config file could not be read ({file.Name}): {ex.Message}", ex);
        }

        CopilotStudioConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<CopilotStudioConfig>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid Copilot Studio config JSON ({file.Name}): {ex.Message}", ex);
        }

        if (cfg is null)
        {
            throw new InvalidOperationException($"Copilot Studio config deserialized to null: {file.FullName}");
        }

        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// Throws if any required field is missing/blank (environmentId, schemaName, tenantId, appClientId), or if
    /// <see cref="Cloud"/> is set to a value that isn't a recognized <see cref="PowerPlatformCloud"/> member name.
    /// </summary>
    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(EnvironmentId)) { missing.Add("environmentId"); }
        if (string.IsNullOrWhiteSpace(SchemaName)) { missing.Add("schemaName"); }
        if (string.IsNullOrWhiteSpace(TenantId)) { missing.Add("tenantId"); }
        if (string.IsNullOrWhiteSpace(AppClientId)) { missing.Add("appClientId"); }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Copilot Studio config is missing required field(s): " + string.Join(", ", missing) +
                ". Required: environmentId, schemaName, tenantId, appClientId.");
        }

        if (!TryResolveCloud(out _))
        {
            throw new InvalidOperationException(
                $"Copilot Studio config field 'cloud' has an unrecognized value: '{Cloud}'. Valid values " +
                "(case-insensitive): " + string.Join(", ", ValidCloudNames) + ".");
        }
    }

    /// <summary>
    /// Resolves <see cref="Cloud"/> to the SDK's <see cref="PowerPlatformCloud"/> enum — <see cref="PowerPlatformCloud.Prod"/>
    /// when <see cref="Cloud"/> is null/blank, matched case-insensitively against the enum's member names otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Cloud"/> is set to a value that isn't a recognized <see cref="PowerPlatformCloud"/> member name.
    /// Call <see cref="Validate"/> first (as <see cref="Load"/> and <see cref="CopilotStudioAgentFactory.BuildLive"/>
    /// already do) so this never fires on an already-validated config.
    /// </exception>
    public PowerPlatformCloud ResolveCloud()
    {
        if (TryResolveCloud(out var cloud))
        {
            return cloud;
        }

        throw new InvalidOperationException(
            $"Copilot Studio config field 'cloud' has an unrecognized value: '{Cloud}'. Valid values " +
            "(case-insensitive): " + string.Join(", ", ValidCloudNames) + ".");
    }

    private bool TryResolveCloud(out PowerPlatformCloud cloud)
    {
        if (string.IsNullOrWhiteSpace(Cloud))
        {
            cloud = PowerPlatformCloud.Prod;
            return true;
        }

        // Enum.TryParse matches C# member names, which are identical to this enum's [EnumMember] JSON names
        // (e.g. "DoD", "GovFR") — no separate JSON-attribute-aware lookup is needed. Unknown (-1) is a real
        // enum value but not a valid *input* — a config never means "explicitly unresolvable cloud".
        return Enum.TryParse(Cloud, ignoreCase: true, out cloud) && cloud != PowerPlatformCloud.Unknown;
    }

    private static IEnumerable<string> ValidCloudNames =>
        Enum.GetNames<PowerPlatformCloud>().Where(n => n != nameof(PowerPlatformCloud.Unknown));
}
