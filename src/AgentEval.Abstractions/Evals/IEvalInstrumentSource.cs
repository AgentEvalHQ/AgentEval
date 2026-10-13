// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Evals;

/// <summary>Declares nested evaluation instruments used by an evaluator with its own configuration.</summary>
/// <remarks>These instruments are implementation dependencies, not additional emitted result-tree nodes.
/// Expose every configured instrument; declared configuration must cover all remaining instance settings.
/// Capture must not execute instruments or infer their configuration from outcomes.</remarks>
public interface IEvalInstrumentSource : IEvalConfigurationSource
{
    /// <summary>Gets the configured instruments in deterministic order.</summary>
    IReadOnlyList<IEval> DefinitionInstruments { get; }
}
