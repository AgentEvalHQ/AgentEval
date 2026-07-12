// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Connection settings for a Microsoft Copilot Studio (MCS) agent evaluated via
/// <c>redteam --sut copilot-studio --copilotstudio-config &lt;file.json&gt;</c>. This identifies a <b>non-prod</b>
/// published agent and the Entra app used for Direct-to-Engine (Power Platform API) auth. It carries <b>no secret</b>
/// — the token is acquired at run time (device-code → persisted MSAL cache) and never stored here.
/// </summary>
/// <remarks>
/// The live connector (the actual Copilot Studio client) is wired in a later phase; this config type + loader ship in
/// the scaffold so the CLI surface, validation, and the (credential-free) CI path are complete and testable now.
/// </remarks>
internal sealed record CopilotStudioConfig
{
    /// <summary>The Power Platform environment id hosting the agent.</summary>
    public string EnvironmentId { get; init; } = "";

    /// <summary>The agent's schema name (the MCS bot identifier), e.g. <c>cr1a2_myAgent</c>.</summary>
    public string SchemaName { get; init; } = "";

    /// <summary>The Entra tenant id the agent + app registration live in.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The Entra app-registration client id used for device-code auth (never a secret — public client).</summary>
    public string AppClientId { get; init; } = "";

    /// <summary>Optional Power Platform cloud, e.g. <c>Prod</c> (default), <c>Gov</c>, <c>High</c>.</summary>
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

        CopilotStudioConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<CopilotStudioConfig>(File.ReadAllText(file.FullName), JsonOpts);
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

    /// <summary>Throws if any required field is missing/blank. Required: environmentId, schemaName, tenantId, appClientId.</summary>
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
    }
}
