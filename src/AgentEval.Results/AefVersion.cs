// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;

namespace AgentEval.Results;

/// <summary>
/// The AEF version this library implements, <c>1.0</c> ([VER-1]), and what a document's <c>schemaVersion</c>
/// (<c>MAJOR.MINOR</c> in decimal without leading zeros, contracts/aef/1/spec/07-versioning.md) declares against it.
/// </summary>
public static class AefVersion
{
    /// <summary>The version this library reads and writes: <c>1.0</c>.</summary>
    public const string Current = "1.0";

    /// <summary>
    /// Whether <paramref name="schemaVersion"/> declares a later minor of major 1 than this version ([VER-6]): a string
    /// <c>1.</c> and a minor above 0 written without a leading zero (<c>1.1</c>, <c>1.12</c>), compared as the number it
    /// reads ([VER-1]). Anything else, an absent value included, declares no later minor (<c>1.00</c> or <c>1.01</c>
    /// is no version at all: the reader schema refuses it, R5N-4). Only such a document can make a checkpoint's lane
    /// <c>unverifiable</c> ([CKP-8]): one that declares this version and holds a value this version does not know is read
    /// as §7.3 says.
    /// </summary>
    public static bool DeclaresLaterMinor(JsonNode? schemaVersion) => DeclaresLaterMinor(AefNode.String(schemaVersion));

    /// <inheritdoc cref="DeclaresLaterMinor(JsonNode?)"/>
    public static bool DeclaresLaterMinor(string? schemaVersion)
    {
        if (schemaVersion is null || !schemaVersion.StartsWith("1.", StringComparison.Ordinal) || schemaVersion.Length == 2)
        {
            return false;
        }

        // [VER-1]: 0, or a digit 1-9 and then digits; any minor but 0 is later.
        var minor = schemaVersion.AsSpan(2);
        return minor[0] is >= '1' and <= '9' && !minor.ContainsAnyExceptInRange('0', '9');
    }
}
