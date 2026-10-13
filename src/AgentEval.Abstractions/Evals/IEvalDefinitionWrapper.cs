// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Evals;

/// <summary>Exposes the configured instrument inside an evaluation wrapper for pre-execution identity capture.</summary>
/// <remarks>All configurable evaluation settings must reside in <see cref="DefinitionInner"/>.
/// Wrapper-specific input framing and fixed logic are identified by the wrapper implementation.
/// The inner evaluator must preserve the wrapper's result key and result-tree structure.
/// Wrappers with additional configurable state must not implement this contract without separately binding it.</remarks>
public interface IEvalDefinitionWrapper : IEval
{
    /// <summary>The actual configured evaluator used by this wrapper, without invoking it.</summary>
    IEval DefinitionInner { get; }
}
