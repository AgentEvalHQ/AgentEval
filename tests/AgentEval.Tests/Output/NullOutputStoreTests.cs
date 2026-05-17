// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

public class NullOutputStoreTests
{
    private static readonly NullOutputStore Store = new();

    [Fact]
    public async Task EnsureSolution_ReturnsStub()
    {
        var result = await Store.EnsureSolutionAsync();

        Assert.Equal(Guid.Empty, result.Id);
        Assert.Equal("<null>", result.Name);
        Assert.Equal("", result.Path);
    }

    [Fact]
    public async Task WriteScenarioResult_DoesNothing()
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var context = new RunContext("project", "/path", "xunit", null, null, "eval");
        var manifest = await Store.StartRunAsync(subject, context);

        var scenario = new ScenarioResult(
            "s1", "Scenario 1", "input", "output", true, 1.0,
            new Dictionary<string, double>(),
            new List<AssertionResult>(),
            TimeSpan.Zero, 0.0);

        var exception = await Record.ExceptionAsync(
            () => Store.WriteScenarioResultAsync(manifest.Run.RunId, scenario));

        Assert.Null(exception);
    }
}
