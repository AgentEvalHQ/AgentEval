// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// T3.8 (2026-05-25) — Relay-style Connection<T> shape for cursor-based
// pagination. Hot Chocolate 16's `[UsePaging]` attribute auto-wires this for
// IQueryable / IExecutable sources, but our resolvers expose IAsyncEnumerable
// over an opaque IOutputStoreReader that does not implement IQueryable. The
// hand-rolled shape avoids forcing a (potentially expensive) ToListAsync()
// before pagination can engage.
//
// Cursor encoding: base64 of the integer index into the materialised page
// source. Opaque to callers (per the Relay spec) but deterministic across
// requests AS LONG AS the underlying source is ordered (the resolvers below
// materialise + sort to give that guarantee).

using System.Text;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Relay-style paginated Connection wrapper for a slice of <typeparamref name="T"/> values.
/// </summary>
public sealed record Connection<T>(
    IReadOnlyList<Edge<T>> Edges,
    PageInfo PageInfo,
    int TotalCount);

/// <summary>One edge in a Connection — pairs a node with its cursor.</summary>
public sealed record Edge<T>(T Node, string Cursor);

/// <summary>Standard Relay page-info shape.</summary>
public sealed record PageInfo(
    bool HasNextPage,
    bool HasPreviousPage,
    string? StartCursor,
    string? EndCursor);

/// <summary>Pagination helpers shared by the Connection-returning resolvers.</summary>
internal static class Pagination
{
    /// <summary>Default page size when caller does not specify <c>first</c>.</summary>
    internal const int DefaultFirst = 50;

    /// <summary>Hard cap on <c>first</c> to bound resolver cost.</summary>
    internal const int MaxFirst = 200;

    /// <summary>
    /// Slices <paramref name="source"/> using the supplied <c>first</c> / <c>after</c>
    /// pair and packages the result as a <see cref="Connection{T}"/>. The
    /// caller is responsible for materialising + ordering the source — pagination
    /// is index-based against the materialised list and cursors are an opaque
    /// base64-encoded index. Cursor semantics: "edges AFTER the cursor index";
    /// the first cursor is index 0, so `after=cursor(0)` returns edges
    /// starting at index 1.
    /// </summary>
    internal static Connection<T> Paginate<T>(
        IReadOnlyList<T> source,
        int? first,
        string? after)
    {
        var clampedFirst = Math.Clamp(first ?? DefaultFirst, 1, MaxFirst);

        var startIndex = 0;
        if (!string.IsNullOrEmpty(after) && TryDecodeCursor(after, out var afterIndex))
        {
            startIndex = afterIndex + 1;
        }

        var total = source.Count;
        if (startIndex >= total)
        {
            return new Connection<T>(
                Edges: Array.Empty<Edge<T>>(),
                PageInfo: new PageInfo(false, startIndex > 0, null, null),
                TotalCount: total);
        }

        var endIndex = Math.Min(startIndex + clampedFirst, total);
        var edges = new Edge<T>[endIndex - startIndex];
        for (int i = startIndex; i < endIndex; i++)
        {
            edges[i - startIndex] = new Edge<T>(source[i], EncodeCursor(i));
        }

        return new Connection<T>(
            Edges: edges,
            PageInfo: new PageInfo(
                HasNextPage: endIndex < total,
                HasPreviousPage: startIndex > 0,
                StartCursor: edges.Length > 0 ? edges[0].Cursor : null,
                EndCursor: edges.Length > 0 ? edges[^1].Cursor : null),
            TotalCount: total);
    }

    private static string EncodeCursor(int index)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"i:{index}"));

    private static bool TryDecodeCursor(string cursor, out int index)
    {
        index = -1;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (decoded.StartsWith("i:", StringComparison.Ordinal)
                && int.TryParse(decoded.AsSpan(2), out var parsed)
                && parsed >= 0)
            {
                index = parsed;
                return true;
            }
        }
        catch (FormatException) { /* malformed cursor → treat as start */ }
        return false;
    }
}
