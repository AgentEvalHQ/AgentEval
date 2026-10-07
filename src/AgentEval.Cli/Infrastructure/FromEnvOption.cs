// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// <c>--from-env</c>: build the target from whichever provider <c>AI_INFERENCE_PROVIDER</c> selects. It used to be
/// called <c>--azure-from-env</c>, a name from before provider selection; that name is kept as an alias so no script
/// that passes it breaks.
/// </summary>
internal static class FromEnvOption
{
    public const string Name = "--from-env";

    /// <summary>The old name, still accepted.</summary>
    public const string LegacyAlias = "--azure-from-env";

    public static Option<bool> Create(string description) => new(Name, LegacyAlias) { Description = description };
}
