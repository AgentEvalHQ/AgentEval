// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;

namespace AgentEval.Results.Runner;

/// <summary>Resolved credentials delivered only to the component purpose named by the plan.</summary>
/// <remarks>Contains secrets. It is never serialized, logged, or installed into the process environment.</remarks>
public sealed class AefJobCredentials
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _purposes;
    private static readonly IReadOnlyDictionary<string, string> Empty = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal AefJobCredentials(Dictionary<string, Dictionary<string, string>> purposes) =>
        _purposes = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(purposes.ToDictionary(
            p => p.Key, p => (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(p.Value, StringComparer.Ordinal)), StringComparer.Ordinal));

    /// <summary>The credentials assigned to one purpose; an empty map when none were assigned.</summary>
    public IReadOnlyDictionary<string, string> ForPurpose(string purpose) =>
        _purposes.TryGetValue(purpose, out var credentials) ? credentials : Empty;
}
