// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tests.Core;
using Xunit;

namespace AgentEval.Tests.MAF;

/// <summary>
/// ADR-030 defect D-d / tracker AE-02. <c>TestCase.ExpectedTools</c> is written by every dataset loader
/// and read by no <c>MAFEvaluationHarness</c> path: a dataset declaring <c>expected_tools</c> passed on
/// any non-empty string. Enforcement is deferred to the <c>IEval</c> bridge (AE-04, blocked on AE-06 by
/// ADR-030 §6 Step 5); until then ADR-030 §6.2 rules that the field must not be <i>silently</i>
/// unenforced — a one-line warning names the test case and the tools. This pins the warning, not the
/// (unchanged, flattering) verdict.
/// </summary>
public class MAFEvaluationHarnessExpectedToolsTests
{
    private sealed class PlainAgent(string text) : IStreamableAgent
    {
        public string Name => "PlainAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentResponse { Text = text });

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentResponseChunk { Text = text };
            await Task.CompletedTask;
        }
    }

    private static TestCase WithExpectedTools() => new()
    {
        Name = "catalog-search",
        Input = "Find the price of ABC-123",
        ExpectedTools = new[] { "SearchCatalog", "GetPrice" },
    };

    [Fact]
    public async Task ExpectedTools_DeclaredButUnenforced_IsWarned_NotSilent()
    {
        var logger = new ToolUsageExtractorApprovalTests.CapturingLogger();
        var harness = new MAFEvaluationHarness(evaluator: null, logger);

        await harness.RunEvaluationAsync(new PlainAgent("The price is 9.99"), WithExpectedTools());

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(TestCase.ExpectedTools), StringComparison.Ordinal));
        Assert.Contains("catalog-search", warning.Message, StringComparison.Ordinal);
        Assert.Contains("SearchCatalog", warning.Message, StringComparison.Ordinal);
        Assert.Contains("GetPrice", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not enforced", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpectedTools_StreamingPath_IsWarnedToo()
    {
        var logger = new ToolUsageExtractorApprovalTests.CapturingLogger();
        var harness = new MAFEvaluationHarness(evaluator: null, logger);

        await harness.RunEvaluationStreamingAsync(new PlainAgent("The price is 9.99"), WithExpectedTools());

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(TestCase.ExpectedTools), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoExpectedTools_NoWarning()
    {
        var logger = new ToolUsageExtractorApprovalTests.CapturingLogger();
        var harness = new MAFEvaluationHarness(evaluator: null, logger);

        await harness.RunEvaluationAsync(new PlainAgent("ok"), new TestCase { Name = "plain", Input = "hi" });
        await harness.RunEvaluationAsync(new PlainAgent("ok"), new TestCase { Name = "empty-list", Input = "hi", ExpectedTools = Array.Empty<string>() });

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(TestCase.ExpectedTools), StringComparison.Ordinal));
    }
}
