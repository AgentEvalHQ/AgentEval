// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// The run settings an AEF run of a Microsoft 365 agent records in its <c>config.settings</c>. The agenteval CLI
/// (<c>--sut microsoft365</c>) and the Microsoft 365 demo both take them from here, so every host records the same keys
/// with the same values. Every value is content-free: no tenant id, no app id, no prompt and no answer.
/// </summary>
public static class Microsoft365RunSettings
{
    /// <summary>The A2A protocol version the provider speaks (<see cref="Microsoft365Evidence.A2AVersion"/>).</summary>
    public const string A2AVersionKey = "a2aVersion";

    /// <summary>The diagnostic profile in effect, as the <see cref="DiagnosticProfile"/> member name.</summary>
    public const string ProfileKey = "m365Profile";

    /// <summary>The version of the evidence normaliser (<see cref="Microsoft365Evidence.NormalizerVersion"/>).</summary>
    public const string NormalizerVersionKey = "normalizerVersion";

    /// <summary>The provider profile and decoder version (<see cref="Microsoft365Evidence.ProviderProfileVersion"/>).</summary>
    public const string ProviderProfileVersionKey = "providerProfileVersion";

    /// <summary>
    /// The identity hash of the config (<see cref="Microsoft365Config.ComputeIdentitySha256"/>). It is the
    /// <c>configSha256</c> every Work IQ issue carries, so a run can be matched with its trace and issue files.
    /// </summary>
    public const string ConfigSha256Key = "m365ConfigSha256";

    /// <summary>The opaque agent id, or <see cref="DefaultAgentIdValue"/>.</summary>
    public const string AgentIdKey = "m365AgentId";

    /// <summary>The <see cref="AgentIdKey"/> value when the config targets the default Work IQ agent (no agent id).</summary>
    public const string DefaultAgentIdValue = "default";

    /// <summary>The host of the configured endpoint, where the agent card is discovered.</summary>
    public const string EndpointHostKey = "m365EndpointHost";

    /// <summary>The <see cref="EvidenceContentPolicy"/> the evidence documents of the run were projected under.</summary>
    public const string ContentPolicyKey = "m365ContentPolicy";

    /// <summary>
    /// The settings of a run against <paramref name="config"/>: the A2A version, the profile in effect, the normaliser
    /// and decoder versions, the identity hash of the config, the agent id and the endpoint host. The content policy is
    /// added only when <paramref name="contentPolicy"/> is given: a run that writes no evidence document claims none.
    /// </summary>
    /// <param name="config">The config the run used (after any agent id override).</param>
    /// <param name="profile">The diagnostic profile in effect (an override wins over the config's own profile).</param>
    /// <param name="contentPolicy">The content policy of the run's evidence documents; null when the run writes none.</param>
    /// <returns>A new object the caller owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The config's endpoint is not an absolute https URL (validate the config first).</exception>
    public static JsonObject For(Microsoft365Config config, DiagnosticProfile profile, EvidenceContentPolicy? contentPolicy)
    {
        ArgumentNullException.ThrowIfNull(config);
        var settings = new JsonObject
        {
            [A2AVersionKey] = Microsoft365Evidence.A2AVersion,
            [ProfileKey] = profile.ToString(),
            [NormalizerVersionKey] = Microsoft365Evidence.NormalizerVersion,
            [ProviderProfileVersionKey] = Microsoft365Evidence.ProviderProfileVersion,
            [ConfigSha256Key] = config.ComputeIdentitySha256(),
            [AgentIdKey] = string.IsNullOrWhiteSpace(config.AgentId) ? DefaultAgentIdValue : config.AgentId.Trim(),
            [EndpointHostKey] = config.ResolveEndpointUri().Host,
        };
        if (contentPolicy is { } policy)
        {
            settings[ContentPolicyKey] = policy.ToString();
        }

        return settings;
    }
}
