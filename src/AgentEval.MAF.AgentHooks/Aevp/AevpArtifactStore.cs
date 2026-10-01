// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace AgentEval.MAF.AgentHooks.Aevp;

/// <summary>
/// Where emitted AEVP profiles are kept, so that the <c>sha256:</c> address in a verdict's evidence resolves to the
/// exact bytes it names.
/// </summary>
/// <remarks>
/// The interceptor writes each profile here <i>before</i> it returns the address. An address that nothing stores
/// is a pointer to nothing. Implement this over durable storage (a blob container, a content-addressed store) when
/// auditors outside the process must resolve it.
/// </remarks>
public interface IAevpArtifactStore
{
    /// <summary>Stores a profile's canonical bytes under their content address.</summary>
    /// <param name="contentAddress">The <c>sha256:&lt;hex&gt;</c> address of <paramref name="canonicalJson"/>.</param>
    /// <param name="canonicalJson">The RFC 8785 canonical JSON the address was computed over.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PutAsync(string contentAddress, string canonicalJson, CancellationToken cancellationToken = default);

    /// <summary>Resolves an address to the canonical JSON stored under it.</summary>
    bool TryGet(string contentAddress, [NotNullWhen(true)] out string? canonicalJson);
}

/// <summary>
/// The default <see cref="IAevpArtifactStore"/>: an in-process dictionary. Resolvable for the lifetime of the
/// process only.
/// </summary>
/// <remarks>
/// Profiles are small and content-addressed, so identical profiles are stored once. A gate set emits only a handful
/// of distinct profiles (one per deciding policy, plus the unevaluated ones), and the store stays that size.
/// </remarks>
public sealed class InMemoryAevpArtifactStore : IAevpArtifactStore
{
    private readonly ConcurrentDictionary<string, string> _items = new(StringComparer.Ordinal);

    /// <summary>Distinct profiles stored.</summary>
    public int Count => _items.Count;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The address is not the content address of the JSON. A store that accepted
    /// it would resolve the address to bytes that do not hash to it.</exception>
    public ValueTask PutAsync(string contentAddress, string canonicalJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentAddress);
        ArgumentNullException.ThrowIfNull(canonicalJson);
        if (!string.Equals(AgentEvidenceProfile.AddressOf(canonicalJson), contentAddress, StringComparison.Ordinal))
        {
            throw new ArgumentException("The content address does not match the SHA-256 of the canonical JSON.", nameof(contentAddress));
        }

        _items.TryAdd(contentAddress, canonicalJson);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public bool TryGet(string contentAddress, [NotNullWhen(true)] out string? canonicalJson) =>
        _items.TryGetValue(contentAddress, out canonicalJson);
}
