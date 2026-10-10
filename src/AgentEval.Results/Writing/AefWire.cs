// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Writing;

/// <summary>The value an enum member is written as: the writer schema's enum value it stands for ([VER-2]).</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class AefNameAttribute(string name) : Attribute
{
    /// <summary>The value as the writer schema lists it.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// How the writer turns model values into JSON values: enum members into the schema's names, times into [ENC-8]
/// timestamps, numbers into finite binary64 values ([ENC-3]), digests into [ENC-11]'s forms.
/// </summary>
internal static class AefWire
{
    private static readonly ConcurrentDictionary<Enum, string> Names = new();

    /// <summary>The writer schema's name for an enum member.</summary>
    /// <exception cref="ArgumentException">A value that is not a named member (a cast integer).</exception>
    public static string Name<T>(T value)
        where T : struct, Enum => Names.GetOrAdd(value, static v =>
        {
            var field = v.GetType().GetField(v.ToString(), BindingFlags.Public | BindingFlags.Static);
            return field?.GetCustomAttribute<AefNameAttribute>()?.Name
                   ?? throw new ArgumentException($"{v.GetType().Name} has no member {v}: a writer writes only known values ([VER-2]).");
        });

    /// <summary>A string value of the enum's name.</summary>
    public static JsonNode Node<T>(T value)
        where T : struct, Enum => JsonValue.Create(Name(value));

    /// <summary>
    /// A time as AEF writes it ([ENC-8]): UTC, <c>yyyy-MM-ddTHH:mm:ss</c>, the fraction of the second only when it is not
    /// zero and without trailing zeros (up to nine digits, at the full precision given: n2-d), then <c>Z</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Not an [ENC-8] time: nanoseconds beyond 0 to 999,999,999, or a year beyond 0001 to 9999.</exception>
    public static string Time(AefTime time) => time.IsValid
        ? time.ToString()
        : throw new ArgumentException($"{time} is not an AEF time: nanoseconds 0 to 999,999,999, in the years 0001 to 9999 ([ENC-8]).", nameof(time));

    /// <summary>A number as AEF writes it: finite ([ENC-3]).</summary>
    /// <exception cref="ArgumentException">NaN or an infinity.</exception>
    public static JsonNode Number(double value, string field)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"{field} is {value.ToString(CultureInfo.InvariantCulture)}: JSON has no NaN or infinity ([ENC-3]).", field);
        }

        return JsonValue.Create(value);
    }

    /// <summary>An integer as AEF writes it: plain digits ([ENC-4]), within ±(2^53 − 1).</summary>
    /// <exception cref="ArgumentException">Beyond ±(2^53 − 1), which a binary64 reader would not read exactly.</exception>
    public static JsonNode Integer(long value, string field)
    {
        const long max = 9_007_199_254_740_991;
        if (value is > max or < -max)
        {
            throw new ArgumentException($"{field} is {value}: an integer is at most 2^53 − 1 in magnitude ([ENC-4]).", field);
        }

        return JsonValue.Create(value);
    }

    /// <summary>The SHA-256 of the bytes, lower-case hex ([ENC-11]: the bare form of blob names and run hashes).</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A copy of a producer's <c>ext</c> object ([ENC-19]), or null.</summary>
    public static JsonObject? Ext(JsonObject? ext) => ext?.DeepClone().AsObject();

    /// <summary>Adds a member when the value is given.</summary>
    public static void Put(this JsonObject json, string name, JsonNode? value)
    {
        if (value is not null)
        {
            json[name] = value;
        }
    }

    /// <summary>Adds a string member when the value is given.</summary>
    public static void Put(this JsonObject json, string name, string? value)
    {
        if (value is not null)
        {
            json[name] = value;
        }
    }

    /// <summary>Adds a number member when the value is given (finite, [ENC-3]).</summary>
    public static void Put(this JsonObject json, string name, double? value)
    {
        if (value is { } number)
        {
            json[name] = Number(number, name);
        }
    }

    /// <summary>Adds an integer member when the value is given (plain digits, [ENC-4]).</summary>
    public static void Put(this JsonObject json, string name, long? value)
    {
        if (value is { } number)
        {
            json[name] = Integer(number, name);
        }
    }

    /// <summary>Adds a boolean member when the value is given.</summary>
    public static void Put(this JsonObject json, string name, bool? value)
    {
        if (value is { } flag)
        {
            json[name] = flag;
        }
    }

    /// <summary>An array of strings.</summary>
    public static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>An array of the items' JSON.</summary>
    public static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode> json) => new([.. items.Select(i => (JsonNode?)json(i))]);
}
