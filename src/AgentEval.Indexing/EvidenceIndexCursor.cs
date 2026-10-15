// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Indexing;

// Shared bounded canonical wire format. Family-specific shape/snapshot checks remain with each query.
internal static class EvidenceIndexCursor
{
    internal static string Encode<T>(T cursor)
    {
        var text = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (text.Length > 65536) throw new EvidenceIndexException("cursor-size-limit");
        return text;
    }

    internal static T Decode<T>(string text) where T : class
    {
        if (text.Length is 0 or > 65536 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new EvidenceIndexException("invalid-cursor");
        try
        {
            var encoded = text.Replace('-', '+').Replace('_', '/');
            var cursor = JsonSerializer.Deserialize<T>(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')),
                new JsonSerializerOptions { MaxDepth = 4 });
            if (cursor is null || Encode(cursor) != text) throw new EvidenceIndexException("invalid-cursor");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException) { throw new EvidenceIndexException("invalid-cursor"); }
    }
}
