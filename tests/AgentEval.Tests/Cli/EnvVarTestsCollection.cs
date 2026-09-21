// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Clears every inference-provider variable for as long as this collection runs, and restores them after.
/// </summary>
/// <remarks>
/// <para>
/// These tests describe an environment by setting the two or three variables they care about. That was
/// sufficient while the CLI only knew Azure OpenAI: scrubbing <c>AZURE_OPENAI_*</c> meant "no model
/// configured". Now that the CLI resolves <c>AI_INFERENCE_PROVIDER</c>, an ambient <c>OPENAI_API_KEY</c>,
/// <c>BITDEER_API_KEY</c> or Foundry variable on the developer's machine or the CI runner configures a
/// provider the test believed it had removed — and the test fails on the environment rather than on the code.
/// </para>
/// <para>
/// The fixture is built once for the whole collection (which already runs serially), so it costs one scrub
/// and one restore. A test that wants a provider still sets its variables explicitly and is unaffected; a
/// test that wants none now actually gets none, on any machine.
/// </para>
/// </remarks>
public sealed class ProviderEnvironmentFixture : IDisposable
{
    private readonly ProviderEnvironmentScope _scope = new();

    public void Dispose() => _scope.Dispose();
}

/// <summary>
/// Serialises every test that reads or writes an inference-provider environment variable, and hands them an
/// environment with none of those variables set.
/// </summary>
[CollectionDefinition("EnvVarTests", DisableParallelization = true)]
public sealed class EnvVarTestsCollection : ICollectionFixture<ProviderEnvironmentFixture>
{
}
