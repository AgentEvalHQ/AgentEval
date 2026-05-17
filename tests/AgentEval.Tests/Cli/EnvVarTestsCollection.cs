// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// xUnit collection marker for tests that mutate process-global environment
/// variables (AZURE_OPENAI_*, AGENTEVAL_ALLOW_STUB_JUDGE, etc.). Disables
/// parallelisation so the scrub-set-act-restore sequences in different test
/// classes don't race on the same env vars.
/// </summary>
/// <remarks>
/// xUnit's default is to run different test classes in parallel; this
/// collection forces them onto a single serial queue. Within a class,
/// xUnit already serialises tests, so this only matters when multiple classes
/// reach the same shared process state.
/// </remarks>
[CollectionDefinition("EnvVarTests", DisableParallelization = true)]
public sealed class EnvVarTestsCollection
{
    // Marker class — no fixture state. xUnit discovers the
    // CollectionDefinition attribute and applies it to every test class
    // decorated with [Collection("EnvVarTests")].
}
