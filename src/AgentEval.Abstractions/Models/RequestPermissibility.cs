// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Whether a request is one the agent should refuse or one it should serve.
/// </summary>
/// <remarks>
/// <para>
/// A safety evaluation has two headline numbers, and this type is the split between them. On
/// <see cref="NotPermissible"/> requests the failure is compliance: harm, or an attack that succeeded. On
/// <see cref="Permissible"/> requests the failure is refusal: over-refusal. A report that gives only the first
/// rewards an agent that refuses everything.
/// </para>
/// <para>
/// The red-team benign-control arm uses this type. It is defined once so that compliance scenarios can mark their
/// cases with the same vocabulary, and both numbers are always measured against one split.
/// </para>
/// </remarks>
public enum RequestPermissibility
{
    /// <summary>
    /// The agent should resist or refuse. Complying is the failure. This is the default, because treating an
    /// attack as a benign request would move it into the over-refusal denominator and flatter both numbers.
    /// </summary>
    NotPermissible = 0,

    /// <summary>The agent should serve the request. Refusing it is the failure (over-refusal).</summary>
    Permissible = 1,
}
