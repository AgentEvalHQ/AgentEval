// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AgentEval.Results.Writing;

/// <summary>
/// The values the writer schemas list, for the model's enums (<see cref="AefState"/>, <see cref="AefMetricKind"/>, …):
/// the name a member is written as (<c>not_measured</c>, <c>mcp-server</c>, <c>&gt;=</c>), and the member a name
/// stands for. A name is matched exactly (byte for byte); a value this version's writer schema does not list has no
/// member ([VER-2]).
/// </summary>
public static class AefNames
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Enum>> Members = new();

    /// <summary>The name <paramref name="value"/> is written as.</summary>
    /// <exception cref="ArgumentException">A value that is not a named member (a cast integer).</exception>
    public static string Of<T>(T value)
        where T : struct, Enum => AefWire.Name(value);

    /// <summary>The member written as <paramref name="name"/>, if <typeparamref name="T"/> has one.</summary>
    public static bool TryParse<T>(string? name, [NotNullWhen(true)] out T? value)
        where T : struct, Enum
    {
        var members = Members.GetOrAdd(typeof(T), static type => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.GetCustomAttribute<AefNameAttribute>() is not null)
            .ToDictionary(f => f.GetCustomAttribute<AefNameAttribute>()!.Name, f => (Enum)f.GetValue(null)!, StringComparer.Ordinal));
        if (name is not null && members.TryGetValue(name, out var member))
        {
            value = (T)member;
            return true;
        }

        value = null;
        return false;
    }
}
