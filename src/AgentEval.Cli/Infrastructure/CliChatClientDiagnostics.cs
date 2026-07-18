// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// The single composing hook every <c>IChatClient</c>-constructing call site in the CLI should route through
/// — applies <c>--log-file</c> (<see cref="VerboseLog.Wrap"/>) and <c>--capture-fixture</c>
/// (<see cref="FixtureCapture.Wrap"/>) independently and composably: either, both, or neither may be active
/// for a given invocation, and each flag's own on/off state is exactly what decides whether ITS wrap is a
/// no-op — this method never has to know which. Introduced so a new diagnostic sink (this one, and any
/// future one) doesn't require re-auditing every call site that previously called <see cref="VerboseLog.Wrap"/>
/// directly.
/// </summary>
internal static class CliChatClientDiagnostics
{
    /// <summary>Applies every active CLI diagnostic wrap to <paramref name="client"/>, in order.</summary>
    /// <param name="client">The chat client to observe.</param>
    /// <param name="label">A short role label (e.g. "sut"/"judge"/"attacker") passed to every active wrap.</param>
    public static IChatClient Wrap(IChatClient client, string? label = null) =>
        FixtureCapture.Wrap(VerboseLog.Wrap(client, label), label);
}
