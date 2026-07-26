// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Controls only the model-visible presentation of an enforced Gatekeeper refusal. It never changes the
/// verdict, containment state, evidence, retry count, or operator-visible reason.
/// </summary>
public enum GatekeeperRefusalStyle
{
    /// <summary>
    /// Emit the versioned <c>gatekeeper.refusal/1</c> envelope with its opaque evidence reference.
    /// This preserves the existing Gatekeeper behavior.
    /// </summary>
    Structured,

    /// <summary>
    /// Emit a validated caller-owned generic failure message while retaining the complete structured refusal
    /// and reason in operator evidence. Phase 3 containment wiring supplies the runtime presentation behavior.
    /// </summary>
    Camouflaged,
}
