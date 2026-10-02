// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Xunit;

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that auto-skips the test when no inference provider is configured
/// (resolved like the AgentEval CLI and samples: <c>AI_INFERENCE_PROVIDER</c>, for example Bitdeer). Works with all xUnit v2 runners including <c>dotnet test</c>
/// (VSTest), because the <see cref="FactAttribute.Skip"/> property is evaluated at
/// discovery time rather than at execution time.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SkipIfNotConfiguredFact : FactAttribute
{
    public SkipIfNotConfiguredFact()
    {
        if (!NuGetConsumer.Config.IsConfigured)
            Skip = NuGetConsumer.Config.NotConfiguredMessage;
    }
}
