// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Computes a stable fingerprint of the FROZEN gate configuration a run enforced (Phase 3, P3-6), so every
/// <see cref="GateEvidence"/> record — and the end-of-run receipt — can be tied to the exact gate list that
/// produced it. The fingerprint is <b>order-sensitive</b>: gate order is semantically significant (first-block-wins,
/// mutate-then-revalidate), so two lists differing only in order are different configurations. Reuses the shared
/// <see cref="ManifestFingerprint.Hash"/> (SHA-256) so it matches the rest of the toolkit's fingerprinting.
/// </summary>
public static class GateConfigFingerprint
{
    /// <summary>Fingerprints the tool / result / chat gate lists (any may be null/empty). Each gate contributes its concrete type and policy name, in order.</summary>
    public static string Compute(
        IReadOnlyList<IToolGate>? toolGates = null,
        IReadOnlyList<IToolResultGate>? resultGates = null,
        IReadOnlyList<IChatGate>? chatGates = null)
    {
        var sb = new StringBuilder();
        AppendList(sb, "tool", toolGates?.Select(g => Describe(g, g.GetType(), g.PolicyName)));
        AppendList(sb, "result", resultGates?.Select(g => Describe(g, g.GetType(), g.PolicyName)));
        AppendList(sb, "chat", chatGates?.Select(g => Describe(g, g.GetType(), g.PolicyName)));
        return ManifestFingerprint.Hash(sb.ToString());
    }

    private static string Describe(object gate, Type gateType, string policyName)
    {
        var description = $"{gateType.FullName}#{policyName}";
        return gate is IConfigurationFingerprintContributor contributor
            ? description + "@" + contributor.ConfigurationFingerprintContribution
            : description;
    }

    private static void AppendList(StringBuilder sb, string label, IEnumerable<string>? entries)
    {
        sb.Append('[').Append(label).Append("]\n");
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            sb.Append(entry).Append('\n');
        }
    }
}

/// <summary>Optional internal contribution for gates whose behavior depends on configuration beyond PolicyName.</summary>
internal interface IConfigurationFingerprintContributor
{
    string ConfigurationFingerprintContribution { get; }
}
