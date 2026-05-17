// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>A single tool/function invocation captured as part of an eval input.</summary>
public sealed record ToolCall(string Name, IReadOnlyDictionary<string, object>? Arguments, string? Result);

/// <summary>Definition of a tool that was available to the agent during the evaluated interaction.</summary>
public sealed record ToolDefinition(string Name, string? Description, IReadOnlyDictionary<string, object>? Parameters);

/// <summary>An expected agentic action used to verify tool-use behaviour.</summary>
public sealed record ExpectedAction(string Description, IReadOnlyList<string>? RequiredTools);

/// <summary>Intentionally permissive input container shared across all eval types.</summary>
public sealed record EvalInput(
    string Query,
    string? Response = null,
    string? Context = null,
    string? GroundTruth = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    IReadOnlyList<ToolDefinition>? ToolDefinitions = null,
    IReadOnlyList<ExpectedAction>? ExpectedActions = null,
    string? SystemMessage = null,
    IReadOnlyDictionary<string, object>? Metadata = null);
