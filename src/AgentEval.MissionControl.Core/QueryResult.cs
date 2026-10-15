// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MissionControl.Core;

/// <summary>Transport-independent query availability; never an evaluation verdict.</summary>
public enum QueryState
{
    /// <summary>The requested page contains data.</summary>
    Available,
    /// <summary>The query succeeded with an empty page; its snapshot metadata is retained.</summary>
    Empty,
    /// <summary>The host has not configured this query capability.</summary>
    NotConfigured,
    /// <summary>The requested source is absent from the current index.</summary>
    NotFound,
    /// <summary>The request or continuation is invalid for this query.</summary>
    InvalidRequest,
    /// <summary>The index changed; the caller must restart traversal.</summary>
    StaleCursor,
    /// <summary>A finite query or index resource budget refused the operation.</summary>
    ResourceLimit,
    /// <summary>The index cannot currently supply a verified page.</summary>
    Unavailable
}

/// <summary>A shared query outcome. Only Available/Empty carry data; refusals carry a stable path-free code.</summary>
public sealed class QueryResult<T> where T : class
{
    internal QueryResult(QueryState state, T? data, string? code) { State = state; Data = data; Code = code; }
    /// <summary>Availability state, distinct from evidence verification and evaluation status.</summary>
    public QueryState State { get; }
    /// <summary>The complete detached page, including provenance and continuation, or null on refusal.</summary>
    public T? Data { get; }
    /// <summary>Allowlisted machine-readable refusal code; never exception text or a filesystem path.</summary>
    public string? Code { get; }
}
