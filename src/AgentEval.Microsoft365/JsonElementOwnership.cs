// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Microsoft365;

/// <summary>
/// Guards the one ownership rule for every <see cref="JsonElement"/> retained by this provider: it must be a
/// detached clone, never an element backed by a <see cref="JsonDocument"/> someone may dispose.
/// </summary>
internal static class JsonElementOwnership
{
    /// <summary>Returns a detached clone of <paramref name="element"/>, or null for null.</summary>
    /// <exception cref="ArgumentException"><paramref name="element"/> is an undefined element (a default struct), which carries no data.</exception>
    public static JsonElement? Detach(JsonElement? element, string parameterName)
    {
        if (element is null)
        {
            return null;
        }

        if (element.Value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("An undefined JsonElement carries no data; pass null for an absent value.", parameterName);
        }

        return element.Value.Clone();
    }
}
