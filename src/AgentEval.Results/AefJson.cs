// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentEval.Results;

/// <summary>Reading AEF values the way every implementation must (contracts/aef/v2/README.md, §2).</summary>
public static class AefJson
{
    /// <summary>
    /// An integer as JSON Schema means it: a writer writes plain digits, and a reader takes an integral number written
    /// another way (2.0, 2e0) as that integer.
    /// </summary>
    /// <exception cref="FormatException">Not a number, or not integral.</exception>
    public static long Integer(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var value = decimal.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return value == decimal.Truncate(value)
            ? (long)value
            : throw new FormatException($"{node.ToJsonString()} is not an integer.");
    }
}
