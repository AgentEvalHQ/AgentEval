// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// Shared setup for all integration tests.
/// The live tests in this project need a configured inference provider (AI_INFERENCE_PROVIDER, e.g. Bitdeer).
/// Use <see cref="SkipIfNotConfiguredFact"/> instead of <c>[Fact]</c>
/// to auto-skip tests when credentials are absent.
/// </summary>
internal static class TestSetup
{
}
