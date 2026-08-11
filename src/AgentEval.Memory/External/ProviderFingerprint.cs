// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;

namespace AgentEval.Memory.External;

/// <summary>
/// Recovers a provider's backend build identifier (OpenAI calls it <c>system_fingerprint</c>) from a
/// response, when the provider returned one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ChatResponse"/> in Microsoft.Extensions.AI.Abstractions 10.7.0 has no
/// <c>SystemFingerprint</c> property — its surface is ConversationId, CreatedAt, FinishReason,
/// Messages, ModelId, RawRepresentation, ResponseId, Text, Usage, ContinuationToken and
/// AdditionalProperties. So there are exactly two places the value can be: the loosely-typed
/// <see cref="ChatResponse.AdditionalProperties"/> bag, or the provider-specific
/// <see cref="ChatResponse.RawRepresentation"/> object (for the OpenAI client, a
/// <c>ChatCompletion</c>, which does carry it). Both are checked, in that order.
/// </para>
/// <para>
/// Absence is reported as null and never as a placeholder. A model that is pinned by name is not
/// pinned by build: two runs against the same deployment can execute on different backend builds,
/// and determinism only holds while the build is unchanged. Carrying the value lets a report state
/// that two runs were incomparable; inventing one would let a report deny it.
/// </para>
/// </remarks>
internal static class ProviderFingerprint
{
    /// <summary>Keys checked in an additional-properties bag, in order.</summary>
    internal static readonly string[] CandidateKeys =
    [
        "system_fingerprint",
        "systemFingerprint",
        "SystemFingerprint"
    ];

    /// <summary>Property names checked on a provider-specific raw response object, in order.</summary>
    internal static readonly string[] CandidateRawProperties =
    [
        "SystemFingerprint",
        "system_fingerprint"
    ];

    /// <summary>Upper bound on a retained fingerprint; providers return short opaque tokens.</summary>
    internal const int MaximumLength = 256;

    /// <summary>
    /// Reads the fingerprint from a chat response, preferring the additional-properties bag and
    /// falling back to reflection over the provider's raw response object. Returns null when the
    /// provider did not supply one.
    /// </summary>
    internal static string? FromChatResponse(ChatResponse? response)
    {
        if (response is null)
            return null;

        var fromProperties = FromAdditionalProperties(response.AdditionalProperties);
        return fromProperties ?? FromRawRepresentation(response.RawRepresentation);
    }

    /// <summary>
    /// Reads the fingerprint from an agent response's additional-properties bag. The agent is the
    /// caller's own, so AgentEval sees only what the adapter chose to surface — null here means
    /// "not reported", not "not present at the provider".
    /// </summary>
    internal static string? FromAgentResponse(IReadOnlyDictionary<string, object?>? additionalProperties)
        => FromAdditionalProperties(additionalProperties);

    private static string? FromAdditionalProperties(IEnumerable<KeyValuePair<string, object?>>? properties)
    {
        if (properties is null)
            return null;

        // Materialise once: the bag is small, and an IEnumerable may not be cheaply re-enumerable.
        var pairs = properties as IReadOnlyDictionary<string, object?>;
        foreach (var key in CandidateKeys)
        {
            object? value = null;
            if (pairs is not null)
            {
                if (!pairs.TryGetValue(key, out value))
                    continue;
            }
            else
            {
                var found = false;
                foreach (var pair in properties)
                {
                    if (!string.Equals(pair.Key, key, StringComparison.Ordinal))
                        continue;
                    value = pair.Value;
                    found = true;
                    break;
                }
                if (!found)
                    continue;
            }

            var normalized = Normalize(value?.ToString());
            if (normalized is not null)
                return normalized;
        }

        return null;
    }

    private static string? FromRawRepresentation(object? raw)
    {
        if (raw is null)
            return null;

        foreach (var name in CandidateRawProperties)
        {
            try
            {
                var property = raw.GetType().GetProperty(name);
                if (property is null || property.GetIndexParameters().Length > 0)
                    continue;

                var normalized = Normalize(property.GetValue(raw)?.ToString());
                if (normalized is not null)
                    return normalized;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A provider object whose getter throws must not fail the run — the fingerprint is
                // diagnostic metadata, and "not available" is a legitimate answer for it.
            }
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length > MaximumLength ? trimmed[..MaximumLength] : trimmed;
    }
}
