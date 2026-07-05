// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How expensive/non-deterministic a gate is to evaluate. Drives where it may run: only
/// <see cref="PureCode"/> / <see cref="Bounded"/> gates belong on the inline tool-invocation hot path;
/// <see cref="Network"/> / <see cref="Llm"/> gates must run in shadow mode (a later milestone) and are
/// rejected at <c>UseAgentEvalToolGate</c> construction.
/// </summary>
public enum GateCost
{
    /// <summary>Pure in-process code (string/collection checks). Safe inline.</summary>
    PureCode,

    /// <summary>Bounded local work (e.g. a ReDoS-bounded regex with a MatchTimeout). Safe inline.</summary>
    Bounded,

    /// <summary>Makes a network call. NOT allowed inline.</summary>
    Network,

    /// <summary>Invokes an LLM judge. NOT allowed inline.</summary>
    Llm,
}
