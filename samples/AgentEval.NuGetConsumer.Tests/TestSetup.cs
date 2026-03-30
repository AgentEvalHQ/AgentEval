// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// Shared setup for all integration tests.
/// All tests in this project require Azure OpenAI credentials.
/// Use <see cref="SkipIfNotConfiguredFact"/> instead of <see cref="Xunit.Sdk.FactAttribute"/>
/// to auto-skip tests when credentials are absent.
/// </summary>
internal static class TestSetup
{
}
