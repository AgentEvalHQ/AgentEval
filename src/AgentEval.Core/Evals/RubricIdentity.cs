// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;

namespace AgentEval.Evals;

/// <summary>Hashes ordered instrument fields with explicit boundaries and null values.</summary>
internal static class RubricIdentity
{
    internal static string Digest(params string?[] fields)
    {
        var normalized = fields.Select(field => field?.Replace("\r\n", "\n", StringComparison.Ordinal)).ToArray();
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)))
            .ToLowerInvariant();
    }
}
